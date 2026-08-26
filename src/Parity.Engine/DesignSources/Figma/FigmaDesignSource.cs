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

    private string? CacheFilePath(DesignRef reference)
    {
        if (_options.CacheDirectory is null) return null;
        var safe = $"{reference.Source}_{reference.NodeId}".Replace(':', '-').Replace('/', '-');
        return Path.Combine(_options.CacheDirectory, $"figma_{safe}.json");
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
        };
    }

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
            var opacity = (GetDouble(fill, "opacity") ?? 1.0) * nodeOpacity;
            return new Rgba(
                To255(GetDouble(c, "r")), To255(GetDouble(c, "g")), To255(GetDouble(c, "b")),
                Math.Clamp((GetDouble(c, "a") ?? 1.0) * opacity, 0, 1));
        }
        return null;

        static byte To255(double? v) => (byte)Math.Clamp(Math.Round((v ?? 0) * 255), 0, 255);
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
            LetterSpacing: GetDouble(style, "letterSpacing"));
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
