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
                $"Figma 回應裡找不到節點 {reference.NodeId}(檔案 {reference.Source})。");
        return FigmaNodeParser.Parse(doc);
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
            throw new HttpRequestException(
                $"Figma API 回 {(int)response.StatusCode} {response.StatusCode}" +
                $"(檔案 {reference.Source}、節點 {reference.NodeId})。" +
                "請確認 FIGMA_TOKEN 有 file_content:read scope、fileKey/nodeId 正確。");

        var json = await response.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("Figma 回應不是合法 JSON。");

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
            CornerRadius: GetDouble(node, "cornerRadius"),
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

    private static double? GetDouble(JsonNode? node, string prop)
    {
        var v = node?[prop];
        if (v is null) return null;
        try { return v.GetValue<double>(); }
        catch (InvalidOperationException) { return null; }
        catch (FormatException) { return null; }
    }
}
