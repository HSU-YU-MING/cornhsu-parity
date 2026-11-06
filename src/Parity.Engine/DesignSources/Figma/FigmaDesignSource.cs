using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Parity.Engine.Model;

namespace Parity.Engine.DesignSources.Figma;

public sealed record FigmaOptions(
    string Token,
    string? CacheDirectory = null,
    bool RefreshCache = false);

/// <summary>
/// Figma REST API 設計來源(規畫書 4.4):
///   GET /v1/files/:key/nodes?ids=:id → 節點 JSON(box、fills、style、padding、itemSpacing…)
/// 抓過的 frame 存本機快取(依 file+node),之後重跑不再打 Figma、也能離線比對。
/// Token 走環境變數/本機 secret,不進 log、不進 URL(header X-Figma-Token)。
/// </summary>
public sealed class FigmaDesignSource : IDesignSource, IDisposable
{
    private readonly FigmaOptions _options;
    private readonly HttpClient _http;

    public FigmaDesignSource(FigmaOptions options, HttpClient? http = null)
    {
        _options = options;
        _http = http ?? new HttpClient();
        _http.BaseAddress ??= new Uri("https://api.figma.com/");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default)
    {
        var raw = await GetRawNodeJsonAsync(reference, ct);
        var doc = raw["nodes"]?[reference.NodeId]?["document"]
            ?? throw new InvalidOperationException(
                $"node {reference.NodeId} is not present in the Figma response (file {reference.Source}).");
        return FigmaNodeParser.Parse(doc);
    }

    /// <summary>
    /// 非 2xx 的提示。每一句都是野外真的把人帶錯方向之後修的:
    ///   - 429 是流量限制不是設定錯,且方案級額度(免費方案)一撞就是「幾天」——把
    ///     Retry-After 換算成人話講(2026-08-11 路線 B:367422 秒 ≈ 4.3 天)。
    ///   - 403 要把「token 過期」列為候選:Figma 個人 token 建立時就選定效期,過期後
    ///     回的是 403,樣子跟 scope 不足/檔案指錯一模一樣(2026-08-26 Codex 補測實踩:
    ///     token 過期,原訊息只指向 scope/key,查了半天)。
    /// </summary>
    internal static string DescribeFailureHint(System.Net.HttpStatusCode status, TimeSpan? retryAfter) => status switch
    {
        System.Net.HttpStatusCode.TooManyRequests =>
            $"Figma is rate-limiting this token{DescribeRetryAfter(retryAfter)}. " +
            "Plan-tier quotas (free plans) can span days — already-fetched frames keep working from .parity/cache.",
        System.Net.HttpStatusCode.Forbidden =>
            "Check that FIGMA_TOKEN has the file_content:read scope, that the token has not expired " +
            "(personal access tokens expire after the lifetime picked at creation), " +
            "and that fileKey/nodeId are correct.",
        _ => "Check that FIGMA_TOKEN has the file_content:read scope and that fileKey/nodeId are correct.",
    };

    /// <summary>Retry-After 的人話:秒數在「分/小時/天」間挑合適的單位;沒有 header 就只說被限流。</summary>
    internal static string DescribeRetryAfter(TimeSpan? retryAfter) => retryAfter switch
    {
        null => "",
        { TotalHours: >= 24 } d => $" — the API says retry in about {d.TotalDays:0.#} day(s)",
        { TotalMinutes: >= 90 } d => $" — the API says retry in about {d.TotalHours:0.#} hour(s)",
        { } d => $" — the API says retry in about {Math.Max(1, d.TotalMinutes):0} minute(s)",
    };

    /// <summary>匯出的圖片大小上限:正常的 frame 用不到,防的是回應異常時把硬碟塞滿。</summary>
    private const long MaxExportBytes = 64 * 1024 * 1024;

    /// <summary>
    /// 把 frame 匯出成 1x 的 PNG(圖片內容比對的參考圖),回傳本機路徑。<c>use_absolute_bounds</c> 讓圖片剛好是 frame 的框,
    /// 不會因為陰影往外長,所以座標就是「節點的框減掉 frame 的原點」。
    /// 兩段請求:先向 Figma 要圖片網址(帶 token),再下載(網址是別的網域的簽章連結,**不帶 token**)。
    /// 匯出會用掉 API 額度,所以落本機快取:抓過的 frame 不再打 API,<c>--refresh</c> 才重抓。
    /// </summary>
    public async Task<string> GetFrameImagePathAsync(DesignRef reference, CancellationToken ct = default)
    {
        var cacheFile = CacheFilePath(reference, ".png")
            ?? throw new InvalidOperationException("exporting a Figma frame needs a cache directory.");
        if (!_options.RefreshCache && File.Exists(cacheFile)) return cacheFile;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"v1/images/{Uri.EscapeDataString(reference.Source)}?ids={Uri.EscapeDataString(reference.NodeId)}&format=png&scale=1&use_absolute_bounds=true");
        request.Headers.Add("X-Figma-Token", _options.Token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Figma API returned {(int)response.StatusCode} {response.StatusCode} exporting node {reference.NodeId} " +
                $"(file {reference.Source}). {DescribeFailureHint(response.StatusCode, response.Headers.RetryAfter?.Delta)}");

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        var url = json?["images"]?[reference.NodeId]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Figma could not render node {reference.NodeId} (file {reference.Source}).");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var imageUri) || imageUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Figma returned an unexpected image address for node {reference.NodeId}.");

        using var download = await _http.GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!download.IsSuccessStatusCode)
            throw new HttpRequestException($"downloading the exported image of node {reference.NodeId} failed: {(int)download.StatusCode} {download.StatusCode}.");
        if (download.Content.Headers.ContentLength > MaxExportBytes)
            throw new InvalidOperationException($"the exported image of node {reference.NodeId} is larger than {MaxExportBytes / (1024 * 1024)} MB.");

        await using var source = await download.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxExportBytes)
                throw new InvalidOperationException($"the exported image of node {reference.NodeId} is larger than {MaxExportBytes / (1024 * 1024)} MB.");
            buffer.Write(chunk, 0, read);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        await File.WriteAllBytesAsync(cacheFile, buffer.ToArray(), ct);
        return cacheFile;
    }

    private async Task<JsonNode> GetRawNodeJsonAsync(DesignRef reference, CancellationToken ct)
    {
        var cacheFile = CacheFilePath(reference);

        if (!_options.RefreshCache && cacheFile is not null && File.Exists(cacheFile))
        {
            var cached = JsonNode.Parse(await File.ReadAllTextAsync(cacheFile, ct));
            if (cached is not null) return cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"v1/files/{Uri.EscapeDataString(reference.Source)}/nodes?ids={Uri.EscapeDataString(reference.NodeId)}");
        request.Headers.Add("X-Figma-Token", _options.Token);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var hint = DescribeFailureHint(response.StatusCode, response.Headers.RetryAfter?.Delta);
            throw new HttpRequestException(
                $"Figma API returned {(int)response.StatusCode} {response.StatusCode} " +
                $"(file {reference.Source}, node {reference.NodeId}). {hint}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("the Figma response is not valid JSON.");

        if (cacheFile is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            await File.WriteAllTextAsync(cacheFile, json, ct);
        }
        return node;
    }

    private string? CacheFilePath(DesignRef reference, string extension = ".json")
    {
        if (_options.CacheDirectory is null) return null;
        var safe = $"{reference.Source}_{reference.NodeId}".Replace(':', '-').Replace('/', '-');
        return Path.Combine(_options.CacheDirectory, $"figma_{safe}{extension}");
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Figma 節點 JSON → DesignNode。獨立成類別方便針對真實 Figma 回應寫測試。</summary>
public static class FigmaNodeParser
{
    public static DesignNode Parse(JsonNode node)
    {
        var type = MapType(node["type"]?.GetValue<string>());
        var box = ParseBox(node["absoluteBoundingBox"]);

        var children = (node["children"] as JsonArray)?
            .Where(c => c is not null && c["visible"]?.GetValue<bool>() != false)
            .Select(c => Parse(c!))
            .ToList() ?? [];

        return new DesignNode(
            Id: node["id"]?.GetValue<string>() ?? "",
            Name: node["name"]?.GetValue<string>() ?? "",
            Type: type,
            Box: box,
            Fill: ParseSolidFill(node["fills"] as JsonArray, node["opacity"]?.GetValue<double>() ?? 1.0),
            Text: type == DesignNodeType.Text ? ParseTypography(node["style"]) : null,
            Padding: ParsePadding(node),
            ItemSpacing: GetDouble(node, "itemSpacing"),
            CornerRadius: ParseCornerRadius(node),
            Children: children)
        {
            Characters = node["characters"]?.GetValue<string>(),
            LayoutMode = node["layoutMode"]?.GetValue<string>(),
            LayoutSizingHorizontal = node["layoutSizingHorizontal"]?.GetValue<string>(),
            LayoutSizingVertical = node["layoutSizingVertical"]?.GetValue<string>(),
            LayoutWrap = node["layoutWrap"]?.GetValue<string>(),
            LayoutPositioning = node["layoutPositioning"]?.GetValue<string>(),
            // Figma 在預設值 MIN 時會省略這兩個欄位:auto-layout 的節點沒寫就是 MIN,不是「不知道」
            PrimaryAxisAlignItems = node["primaryAxisAlignItems"]?.GetValue<string>() ?? AutoLayoutDefault(node),
            CounterAxisAlignItems = node["counterAxisAlignItems"]?.GetValue<string>() ?? AutoLayoutDefault(node),
            Opacity = GetDouble(node, "opacity") ?? 1.0, // Figma 在 1 時會省略
            BorderWidth = ParseBorderWidth(node),
            BorderColor = ParseBorderColor(node),
            Shadows = ParseShadows(node["effects"] as JsonArray),
            Gradient = ParseGradient(node["fills"] as JsonArray, box),
            TextAutoResize = type == DesignNodeType.Text
                ? node["style"]?["textAutoResize"]?.GetValue<string>() ?? "NONE" // Figma 在預設值 NONE 時會省略
                : null,
        };
    }

    /// <summary>
    /// 邊框只看畫成方框的節點:文字的 stroke 是文字描邊,LINE/VECTOR 的 stroke 就是圖形本身,都不是 CSS 的 border。
    /// 沒有可見的 stroke 就是四邊 0。粗細每邊不同時 Figma 給 individualStrokeWeights。
    /// </summary>
    private static Insets? ParseBorderWidth(JsonNode node)
    {
        if (node["type"]?.GetValue<string>() is not ("FRAME" or "RECTANGLE" or "ELLIPSE" or "COMPONENT" or "COMPONENT_SET" or "INSTANCE"))
            return null;
        var visible = (node["strokes"] as JsonArray)?.Any(s => s is not null && s["visible"]?.GetValue<bool>() != false) == true;
        if (!visible) return new Insets(0, 0, 0, 0);
        if (node["individualStrokeWeights"] is { } each)
            return new Insets(GetDouble(each, "top") ?? 0, GetDouble(each, "right") ?? 0, GetDouble(each, "bottom") ?? 0, GetDouble(each, "left") ?? 0);
        var weight = GetDouble(node, "strokeWeight") ?? 0;
        return new Insets(weight, weight, weight, weight);
    }

    private static Rgba? ParseBorderColor(JsonNode node)
        => ParseBorderWidth(node) is { } width && (width.Top > 0 || width.Right > 0 || width.Bottom > 0 || width.Left > 0)
            ? ParseSolidFill(node["strokes"] as JsonArray, 1.0)
            : null;

    /// <summary>可見的 DROP_SHADOW / INNER_SHADOW。Figma 的 radius 就是 CSS 的模糊半徑(它自己的 Inspect 面板也這樣換)。</summary>
    private static List<ShadowSpec> ParseShadows(JsonArray? effects)
    {
        var result = new List<ShadowSpec>();
        foreach (var e in effects ?? [])
        {
            if (e is null || e["visible"]?.GetValue<bool>() == false) continue;
            var kind = e["type"]?.GetValue<string>();
            if (kind is not ("DROP_SHADOW" or "INNER_SHADOW")) continue;
            result.Add(new ShadowSpec(
                GetDouble(e["offset"], "x") ?? 0, GetDouble(e["offset"], "y") ?? 0,
                GetDouble(e, "radius") ?? 0, GetDouble(e, "spread") ?? 0,
                ToRgba(e["color"], 1.0), kind == "INNER_SHADOW"));
        }
        return result;
    }

    /// <summary>
    /// 第一個可見的漸層填色,頭尾色照色標位置排。方向來自 gradientHandlePositions:前兩個點是漸層的起點與終點,
    /// 座標是「節點左上 (0,0)、右下 (1,1)」,所以要乘上框的寬高才是畫面上的方向。換成 CSS 的角度:0 = 往上、90 = 往右。
    /// </summary>
    private static GradientSpec? ParseGradient(JsonArray? fills, Box box)
    {
        foreach (var fill in fills ?? [])
        {
            if (fill is null || fill["visible"]?.GetValue<bool>() == false) continue;
            if (fill["type"]?.GetValue<string>()?.StartsWith("GRADIENT_", StringComparison.Ordinal) != true) continue;
            var stops = (fill["gradientStops"] as JsonArray)?.Where(s => s is not null)
                .OrderBy(s => GetDouble(s, "position") ?? 0).ToList();
            var opacity = GetDouble(fill, "opacity") ?? 1.0;
            var kind = fill["type"]!.GetValue<string>() switch
            {
                "GRADIENT_LINEAR" => "linear",
                "GRADIENT_RADIAL" => "radial",
                "GRADIENT_ANGULAR" => "conic",
                _ => null, // GRADIENT_DIAMOND 在 CSS 沒有對應
            };
            double? angle = null;
            if (kind == "linear" && fill["gradientHandlePositions"] is JsonArray { Count: >= 2 } handles)
            {
                var dx = ((GetDouble(handles[1], "x") ?? 0) - (GetDouble(handles[0], "x") ?? 0)) * box.W;
                var dy = ((GetDouble(handles[1], "y") ?? 0) - (GetDouble(handles[0], "y") ?? 0)) * box.H;
                if (dx != 0 || dy != 0) angle = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
            }
            return (stops is { Count: >= 2 }
                ? new GradientSpec(ToRgba(stops[0]!["color"], opacity), ToRgba(stops[^1]!["color"], opacity))
                : new GradientSpec()) with
            { Kind = kind, Angle = angle };
        }
        return null;
    }

    /// <summary>Figma 色值 0–1 → 0–255。</summary>
    private static Rgba ToRgba(JsonNode? c, double opacity) => new(
        To255(GetDouble(c, "r")), To255(GetDouble(c, "g")), To255(GetDouble(c, "b")),
        Math.Clamp((GetDouble(c, "a") ?? 1.0) * opacity, 0, 1));

    private static byte To255(double? v) => (byte)Math.Clamp(Math.Round((v ?? 0) * 255), 0, 255);

    private static string? AutoLayoutDefault(JsonNode node)
        => node["layoutMode"]?.GetValue<string>() is "HORIZONTAL" or "VERTICAL" ? "MIN" : null;

    private static DesignNodeType MapType(string? figmaType) => figmaType switch
    {
        "FRAME" => DesignNodeType.Frame,
        "GROUP" => DesignNodeType.Group,
        "TEXT" => DesignNodeType.Text,
        "COMPONENT" or "COMPONENT_SET" => DesignNodeType.Component,
        "INSTANCE" => DesignNodeType.Instance,
        "RECTANGLE" or "ELLIPSE" or "VECTOR" or "LINE" or "STAR" or "POLYGON" or "BOOLEAN_OPERATION"
            => DesignNodeType.Shape,
        _ => DesignNodeType.Other,
    };

    private static Box ParseBox(JsonNode? bbox) => bbox is null
        ? default
        : new Box(
            GetDouble(bbox, "x") ?? 0, GetDouble(bbox, "y") ?? 0,
            GetDouble(bbox, "width") ?? 0, GetDouble(bbox, "height") ?? 0);

    /// <summary>取第一個可見的 SOLID fill。Figma 色值 0–1 → 0–255(規畫書 4.4 的 FromFigma)。</summary>
    private static Rgba? ParseSolidFill(JsonArray? fills, double nodeOpacity)
    {
        if (fills is null) return null;
        foreach (var fill in fills)
        {
            if (fill is null) continue;
            if (fill["visible"]?.GetValue<bool>() == false) continue;
            if (fill["type"]?.GetValue<string>() != "SOLID") continue;
            var c = fill["color"];
            if (c is null) continue;
            return ToRgba(c, (GetDouble(fill, "opacity") ?? 1.0) * nodeOpacity);
        }
        return null;
    }

    /// <summary>style.fontSize/fontWeight/letterSpacing/lineHeightPx 直接對應 CSS。</summary>
    private static Typography? ParseTypography(JsonNode? style)
    {
        if (style is null) return null;
        return new Typography(
            FontFamily: style["fontFamily"]?.GetValue<string>(),
            FontSize: GetDouble(style, "fontSize"),
            FontWeight: GetDouble(style, "fontWeight"),
            LineHeight: GetDouble(style, "lineHeightPx"),
            LetterSpacing: GetDouble(style, "letterSpacing"))
        {
            // Figma 在預設值時會省略這些欄位:節點是 Figma 來的,沒寫就是預設值,不是「不知道」。都換成 CSS 的字彙
            // style.fontStyle 是字型樣式的名字("Regular"、"Bold Italic"),斜體時 italic 也會是 true;兩個都看,字型名稱漏標 italic 時才不會比錯
            FontStyle = style["italic"]?.GetValue<bool>() == true
                || style["fontStyle"]?.GetValue<string>() is { } styleName
                    && (styleName.Contains("italic", StringComparison.OrdinalIgnoreCase) || styleName.Contains("oblique", StringComparison.OrdinalIgnoreCase))
                ? "italic" : "normal",
            TextDecoration = style["textDecoration"]?.GetValue<string>() switch
            {
                "UNDERLINE" => "underline",
                "STRIKETHROUGH" => "line-through",
                _ => "none",
            },
            TextTransform = style["textCase"]?.GetValue<string>() switch
            {
                "UPPER" => "uppercase",
                "LOWER" => "lowercase",
                "TITLE" => "capitalize",
                "SMALL_CAPS" or "SMALL_CAPS_FORCED" => "small-caps",
                _ => "none",
            },
            // 文字框隨內容縮放時,框就是文字的大小,對齊沒有視覺意義
            TextAlign = style["textAutoResize"]?.GetValue<string>() == "WIDTH_AND_HEIGHT" ? null
                : style["textAlignHorizontal"]?.GetValue<string>() switch
                {
                    "LEFT" => "left",
                    "CENTER" => "center",
                    "RIGHT" => "right",
                    "JUSTIFIED" => "justify",
                    _ => null,
                },
        };
    }

    /// <summary>auto-layout 的 padding 四邊;沒有 auto-layout 的節點回 null(就不比)。</summary>
    private static Insets? ParsePadding(JsonNode node)
    {
        var top = GetDouble(node, "paddingTop");
        var right = GetDouble(node, "paddingRight");
        var bottom = GetDouble(node, "paddingBottom");
        var left = GetDouble(node, "paddingLeft");
        if (top is null && right is null && bottom is null && left is null) return null;
        return new Insets(top ?? 0, right ?? 0, bottom ?? 0, left ?? 0);
    }

    /// <summary>
    /// 圓角:均一時 Figma 給 cornerRadius;每角不同時只給 rectangleCornerRadii[TL,TR,BR,BL]。
    /// 取 top-left——與實作端(只讀 borderTopLeftRadius)口徑一致,每角不同的細節不比。
    /// </summary>
    private static double? ParseCornerRadius(JsonNode node)
    {
        if (GetDouble(node, "cornerRadius") is { } uniform) return uniform;
        return node["rectangleCornerRadii"] is JsonArray { Count: > 0 } radii
            ? GetArrayDouble(radii, 0)
            : null;

        static double? GetArrayDouble(JsonArray arr, int i)
        {
            try { return arr[i]?.GetValue<double>(); }
            catch (InvalidOperationException) { return null; }
            catch (FormatException) { return null; }
        }
    }

    private static double? GetDouble(JsonNode? node, string prop)
    {
        var v = node?[prop];
        if (v is null) return null;
        try { return v.GetValue<double>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }
}
