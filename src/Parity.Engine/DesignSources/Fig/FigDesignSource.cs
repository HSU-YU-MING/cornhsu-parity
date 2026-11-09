using System.Globalization;
using System.Text.Json.Nodes;
using Parity.Engine.DesignSources.Figma;

namespace Parity.Engine.DesignSources.Fig;

/// <summary>
/// .fig 檔當設計來源(2.5.0,競品清單「不走 REST API 的 Figma 來源」)。免費方案的 REST 額度以月計,CI 每跑一次都要重抓;
/// 讀本機的 .fig 完全不用 API。
///
/// 做法:把 .fig 裡的節點(Figma 內部的欄位名)換成 REST API 回的 JSON 形狀,再交給 <see cref="FigmaNodeParser"/>——
/// 解析規則(預設值、漸層角度、邊框只看方框……)只有一份,兩條來源量出來的數字才會一樣。
/// 節點 id 跟 Figma 一樣(<c>12:34</c>;實例裡的子節點是 <c>I12:34;56:78</c>),frame 照舊用 Figma 網址裡的 node-id。
/// </summary>
public sealed class FigDesignSource : IDesignSource
{
    private readonly Dictionary<string, (DateTime Written, FigDocument Doc)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default)
    {
        var doc = Load(reference.Source);
        var json = doc.ToRestJson(reference.NodeId);
        return Task.FromResult(FigmaNodeParser.Parse(json));
    }

    /// <summary>REST 形狀的節點 JSON(測試與診斷用)。</summary>
    internal JsonObject GetRestJson(string path, string nodeId) => Load(path).ToRestJson(nodeId);

    private FigDocument Load(string path)
    {
        var written = File.GetLastWriteTimeUtc(path);
        if (_cache.TryGetValue(path, out var hit) && hit.Written == written) return hit.Doc;
        if (!File.Exists(path)) throw new FileNotFoundException($"design file not found: {path}", path);
        var (_, message) = FigFile.Read(path);
        var doc = new FigDocument(message, path);
        _cache[path] = (written, doc);
        return doc;
    }
}

/// <summary>解開的 .fig:節點依 guid 索引、依父節點分組(照 Figma 的分數索引排序)。</summary>
internal sealed class FigDocument
{
    private readonly string _path;
    private readonly Dictionary<string, JsonObject> _byGuid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JsonObject>> _children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _styleByKey = new(StringComparer.Ordinal);

    /// <summary>防呆:實例裡再放實例,展開的深度上限(正常的設計檔不會超過十幾層;循環參照會在這裡停下)。</summary>
    private const int MaxInstanceDepth = 32;

    public FigDocument(JsonObject message, string path)
    {
        _path = path;
        foreach (var n in (message["nodeChanges"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Guid(n["guid"]) is not { } id) continue;
            if (Str(n, "phase") == "REMOVED") continue;
            _byGuid[id] = n;
            if (Str(n, "styleType") is not null && Str(n, "key") is { } key) _styleByKey[key] = n;
        }
        foreach (var n in _byGuid.Values)
            if (Guid(n["parentIndex"]?["guid"]) is { } parent)
            {
                if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = [];
                list.Add(n);
            }
        foreach (var list in _children.Values)
            list.Sort((a, b) => string.CompareOrdinal(Str(a["parentIndex"], "position"), Str(b["parentIndex"], "position")));
    }

    private static string? Guid(JsonNode? g)
        => g is JsonObject o && o["sessionID"] is not null && o["localID"] is not null
            ? $"{(uint)o["sessionID"]!}:{(uint)o["localID"]!}"
            : null;

    private static string? Str(JsonNode? o, string key)
        => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonNode? o, string key)
        => o?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    public JsonObject ToRestJson(string nodeId)
    {
        var key = nodeId.Replace('-', ':');
        if (!_byGuid.TryGetValue(key, out var node))
            throw new InvalidOperationException(
                $"node {nodeId} is not in {_path}. Frames in this file: {string.Join(", ", TopFrames().Take(20))}" +
                " — use the node-id from the frame's Figma link (12-34 or 12:34).");
        return Build(node, Parent(node) is { } parent ? AbsoluteOf(parent) : Affine.Identity, "", [], 0);
    }

    /// <summary>每一頁(CANVAS)最上層的 frame,錯誤訊息裡列給使用者挑。</summary>
    internal IEnumerable<string> TopFrames()
        => _byGuid.Values.Where(n => Str(n, "type") == "CANVAS" && n["internalOnly"]?.GetValue<bool>() != true)
            .SelectMany(page => (_children.GetValueOrDefault(Guid(page["guid"])!) ?? [])
                .Where(c => Str(c, "type") is "FRAME" or "SYMBOL" or "SECTION" or "INSTANCE")
                .Select(c => $"{Str(page, "name")} / {Str(c, "name")} ({Guid(c["guid"])})"));

    // ---------- 座標 ----------

    /// <summary>2×3 仿射矩陣 [m00 m01 m02; m10 m11 m12]:節點本地座標 → 父節點座標。</summary>
    private readonly record struct Affine(double M00, double M01, double M02, double M10, double M11, double M12)
    {
        public static readonly Affine Identity = new(1, 0, 0, 0, 1, 0);

        public static Affine Of(JsonNode? t) => t is null ? Identity
            : new(Num(t, "m00") ?? 1, Num(t, "m01") ?? 0, Num(t, "m02") ?? 0, Num(t, "m10") ?? 0, Num(t, "m11") ?? 1, Num(t, "m12") ?? 0);

        public Affine Then(Affine local) => new(
            M00 * local.M00 + M01 * local.M10, M00 * local.M01 + M01 * local.M11, M00 * local.M02 + M01 * local.M12 + M02,
            M10 * local.M00 + M11 * local.M10, M10 * local.M01 + M11 * local.M11, M10 * local.M02 + M11 * local.M12 + M12);

        public (double X, double Y) Apply(double x, double y) => (M00 * x + M01 * y + M02, M10 * x + M11 * y + M12);

        public Affine? Inverse()
        {
            var det = M00 * M11 - M01 * M10;
            if (Math.Abs(det) < 1e-12) return null;
            return new(M11 / det, -M01 / det, (M01 * M12 - M11 * M02) / det, -M10 / det, M00 / det, (M10 * M02 - M00 * M12) / det);
        }
    }

    /// <summary>沿父節點鏈把 transform 乘起來(到頁面為止)。</summary>
    private Affine AbsoluteOf(JsonObject node)
    {
        var chain = new List<JsonObject>();
        for (var n = node; n is not null && Str(n, "type") is not ("CANVAS" or "DOCUMENT"); n = Parent(n))
        {
            chain.Add(n);
            if (chain.Count > 10_000) break;
        }
        var abs = Affine.Identity;
        for (var i = chain.Count - 1; i >= 0; i--) abs = abs.Then(Affine.Of(chain[i]["transform"]));
        return abs;
    }

    private JsonObject? Parent(JsonObject n) => Guid(n["parentIndex"]?["guid"]) is { } p ? _byGuid.GetValueOrDefault(p) : null;

    // ---------- 節點 → REST 形狀 ----------

    /// <summary>一筆覆寫:Path 是從目前的實例範圍往下的 key 路徑(只有巢狀實例才多一段),Fields 是要蓋上去的欄位。</summary>
    private readonly record struct Override(string[] Path, JsonObject Fields);

    /// <summary>
    /// 一個節點換成 REST 的樣子。
    /// <para><paramref name="scope"/> 是目前這個實例範圍裡的覆寫。Figma 的覆寫路徑只在「穿過巢狀實例」時多一段:
    /// 元件裡不管多深的節點,都用它自己的 key 直接指到(路徑只有一段);指到巢狀實例裡面的,第一段是那個巢狀實例。</para>
    /// </summary>
    private JsonObject Build(JsonObject source, Affine parentAbs, string idPrefix, IReadOnlyList<Override> scope, int depth)
    {
        var key = OverrideKey(source);
        var guid = Guid(source["guid"]) ?? "";
        bool Targets(Override o) => o.Path[0] == key || o.Path[0] == guid;

        var node = Merge(source, scope.Where(o => o.Path.Length == 1 && Targets(o)).Select(o => o.Fields));
        var type = Str(node, "type") ?? "";

        // 實例:屬性從元件來,實例自己存的欄位蓋在上面;換過元件(instance swap)的話,只留位置與大小,其餘全看新元件。
        // 實例範圍的覆寫 = 元件裡為這個實例定義的覆寫與衍生資料 + 外層傳進來、穿過這個實例的那幾筆(後面的優先)
        JsonObject? component = null;
        List<Override> inner = [];
        if (type == "INSTANCE" && depth < MaxInstanceDepth)
        {
            var original = Guid(node["symbolData"]?["symbolID"]);
            var swapped = Guid(node["overriddenSymbolID"]) is { } swap && swap != original ? swap : null;
            if ((swapped ?? original) is { } componentId && _byGuid.TryGetValue(componentId, out var c))
            {
                component = c;
                if (swapped is null)
                {
                    inner.AddRange(Overrides(node["symbolData"]?["symbolOverrides"]));
                    inner.AddRange(Overrides(node["derivedSymbolData"]));
                }
                inner.AddRange(scope.Where(o => o.Path.Length > 1 && Targets(o)).Select(o => new Override(o.Path[1..], o.Fields)));
                node = Inherit(c, swapped is null ? node : Identity(node));
                // 路徑只有元件自己的覆寫,作用在實例本身(例如實例改了邊框色、Figma 重算的大小)
                node = Merge(node, inner.Where(o => o.Path.Length == 1 && o.Path[0] == componentId).Select(o => o.Fields));
                node = Merge(node, scope.Where(o => o.Path.Length == 1 && Targets(o)).Select(o => o.Fields));
            }
        }
        node = ResolveStyles(node);

        var abs = parentAbs.Then(Affine.Of(node["transform"]));
        var rest = new JsonObject
        {
            ["id"] = idPrefix.Length == 0 ? guid : $"{idPrefix};{guid}",
            ["name"] = Str(node, "name") ?? "",
            ["type"] = RestType(node, type),
        };
        if (node["visible"] is JsonValue vis && vis.TryGetValue<bool>(out var visible) && !visible) rest["visible"] = false;
        if (Num(node, "opacity") is { } opacity && opacity < 1) rest["opacity"] = opacity;

        var w = Num(node["size"], "x") ?? 0;
        var h = Num(node["size"], "y") ?? 0;
        rest["absoluteBoundingBox"] = Bounds(abs, w, h);

        if (node["fillPaints"] is JsonArray fills) rest["fills"] = Paints(fills);
        if (node["strokePaints"] is JsonArray strokes) rest["strokes"] = Paints(strokes);
        if (Num(node, "strokeWeight") is { } sw) rest["strokeWeight"] = sw;
        if (Str(node, "strokeAlign") is { } align) rest["strokeAlign"] = align;
        if (node["borderStrokeWeightsIndependent"]?.GetValue<bool>() == true)
            rest["individualStrokeWeights"] = new JsonObject
            {
                ["top"] = Num(node, "borderTopWeight") ?? 0,
                ["right"] = Num(node, "borderRightWeight") ?? 0,
                ["bottom"] = Num(node, "borderBottomWeight") ?? 0,
                ["left"] = Num(node, "borderLeftWeight") ?? 0,
            };
        Corners(node, rest);
        if (node["effects"] is JsonArray effects) rest["effects"] = Effects(effects);
        AutoLayout(node, rest);
        if (type == "TEXT") Text(node, rest);

        var children = new JsonArray();
        if (component is not null)
        {
            var prefix = idPrefix.Length == 0 ? $"I{guid}" : $"{idPrefix};{guid}";
            foreach (var kid in _children.GetValueOrDefault(Guid(component["guid"])!) ?? [])
                children.Add(Build(kid, abs, prefix, inner, depth + 1));
        }
        else if (type != "INSTANCE") // 元件不在這個檔案裡(團隊元件庫):內容無從得知
        {
            foreach (var kid in _children.GetValueOrDefault(guid) ?? [])
                children.Add(Build(kid, abs, idPrefix, scope, depth));
        }
        rest["children"] = children;
        return rest;
    }

    /// <summary>元件身上不屬於實例的欄位:身分、位置、元件自己的發布資料。其他(填色、排版、圓角……)都由實例繼承。</summary>
    private static readonly HashSet<string> ComponentOnly = new(StringComparer.Ordinal)
    {
        "guid", "phase", "parentIndex", "transform", "name", "overrideKey", "symbolData", "derivedSymbolData", "isSymbolPublishable",
        "symbolDescription", "componentKey", "publishID", "sharedSymbolVersion", "publishedVersion", "variantPropSpecs",
        "componentPropDefs", "isStateGroup", "stateGroupPropertyValueOrders", "sourceLibraryKey", "key", "version", "userFacingVersion",
    };

    private static JsonObject Inherit(JsonObject component, JsonObject instance)
    {
        var merged = new JsonObject();
        foreach (var (k, v) in component)
            if (!ComponentOnly.Contains(k)) merged[k] = v?.DeepClone();
        Overlay(merged, instance);
        merged["type"] = "INSTANCE";
        return merged;
    }

    /// <summary>
    /// 把一層欄位蓋到 target 上(身分欄位 guid、parentIndex、overrideKey 只在原本沒有時才寫)。
    /// </summary>
    private static void Overlay(JsonObject target, JsonObject layer)
    {
        foreach (var (k, v) in layer)
            if (k is not ("guid" or "guidPath" or "parentIndex" or "overrideKey") || !target.ContainsKey(k))
                target[k] = v?.DeepClone();
    }

    /// <summary>換過元件的實例:只留下實例自己的身分、位置、大小與顯示狀態。</summary>
    private static JsonObject Identity(JsonObject instance)
    {
        var kept = new JsonObject();
        foreach (var k in new[] { "guid", "phase", "parentIndex", "transform", "size", "name", "visible", "opacity", "overrideKey", "overriddenSymbolID" })
            if (instance[k] is { } v) kept[k] = v.DeepClone();
        return kept;
    }

    /// <summary>
    /// 套用樣式:節點存的填色、邊框色、陰影、文字設定是快取,可能是舊的;有引用樣式(styleIdFor…)時以樣式為準。
    /// (真檔校準:Ant Design 的次要按鈕存的是藍色,引用的樣式 Neutral/1 是白色,Figma 畫出來是白色。)
    /// 團隊元件庫的樣式不在檔案裡:找不到就維持節點存的值。
    /// </summary>
    private JsonObject ResolveStyles(JsonObject node)
    {
        JsonObject? cloned = null;
        JsonObject Target() => cloned ??= (JsonObject)node.DeepClone();
        // emptyWhenMissing:樣式本身沒有這個欄位 = 空的(真檔校準:表格列的「◇ / 9th row」填色樣式沒有任何填色,
        // 節點快取的是一個透明度 0.00001 的色塊;Figma 顯示沒有填色)
        void Copy(string styleField, bool emptyWhenMissing, params string[] fields)
        {
            if (Style(node[styleField]) is not { } style) return;
            foreach (var f in fields)
                if (style[f] is { } v) Target()[f] = v.DeepClone();
                else if (emptyWhenMissing) Target()[f] = new JsonArray();
        }
        Copy("styleIdForFill", true, "fillPaints");
        Copy("styleIdForStrokeFill", true, "strokePaints");
        Copy("styleIdForEffect", true, "effects");
        Copy("styleIdForText", false, "fontSize", "fontName", "lineHeight", "letterSpacing", "textCase", "textDecoration");
        return cloned ?? node;
    }

    private JsonObject? Style(JsonNode? reference)
    {
        if (reference is not JsonObject r) return null;
        if (Guid(r["guid"]) is { } g && _byGuid.TryGetValue(g, out var byGuid)) return byGuid;
        return Str(r["assetRef"], "key") is { } k ? _styleByKey.GetValueOrDefault(k) : null;
    }

    /// <summary>覆寫用的 key:新的檔案用 overrideKey,舊的用 guid。</summary>
    private static string? OverrideKey(JsonObject n) => Guid(n["overrideKey"]) ?? Guid(n["guid"]);

    private static IEnumerable<Override> Overrides(JsonNode? list)
        => (list as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => new Override((o["guidPath"]?["guids"] as JsonArray ?? []).Select(Guid).Where(g => g is not null).Select(g => g!).ToArray(), o))
            .Where(o => o.Path.Length > 0);

    /// <summary>淺層合併:覆寫裡有的欄位蓋掉原本的(guid、guidPath 這種身分欄位不蓋)。</summary>
    private static JsonObject Merge(JsonObject source, IEnumerable<JsonObject> overrides)
    {
        var list = overrides.ToList();
        if (list.Count == 0) return source;
        var merged = (JsonObject)source.DeepClone();
        foreach (var o in list) Overlay(merged, o);
        return merged;
    }

    private static string RestType(JsonObject node, string type) => type switch
    {
        "SYMBOL" => "COMPONENT",
        "ROUNDED_RECTANGLE" => "RECTANGLE",
        "REGULAR_POLYGON" => "POLYGON",
        // 群組在 .fig 裡是「跟著內容縮放」的 frame(REST 才叫 GROUP)
        "FRAME" when node["resizeToFit"]?.GetValue<bool>() == true => "GROUP",
        _ => type,
    };

    private static JsonObject Bounds(Affine abs, double w, double h)
    {
        (double X, double Y)[] corners = [abs.Apply(0, 0), abs.Apply(w, 0), abs.Apply(0, h), abs.Apply(w, h)];
        var x = corners.Min(c => c.X);
        var y = corners.Min(c => c.Y);
        return new JsonObject
        {
            ["x"] = Round(x),
            ["y"] = Round(y),
            ["width"] = Round(corners.Max(c => c.X) - x),
            ["height"] = Round(corners.Max(c => c.Y) - y),
        };
    }

    private static double Round(double v) => Math.Round(v, 4);

    private static JsonArray Paints(JsonArray paints)
    {
        var result = new JsonArray();
        foreach (var p in paints.OfType<JsonObject>())
        {
            var paint = new JsonObject { ["type"] = Str(p, "type") };
            if (p["visible"] is JsonValue v && v.TryGetValue<bool>(out var visible) && !visible) paint["visible"] = false;
            if (Num(p, "opacity") is { } op && op < 1) paint["opacity"] = op;
            if (p["color"] is JsonObject c) paint["color"] = c.DeepClone();
            if (p["stops"] is JsonArray stops)
                paint["gradientStops"] = new JsonArray([.. stops.OfType<JsonObject>().Select(s => (JsonNode)new JsonObject
                {
                    ["color"] = s["color"]?.DeepClone(), ["position"] = Num(s, "position") ?? 0,
                })]);
            // 漸層方向:.fig 存的是「節點座標 → 漸層座標」的矩陣,REST 給的是把手位置(節點座標 0–1)。
            // 線性漸層在漸層座標裡從 (0, 0.5) 走到 (1, 0.5),反矩陣換回節點座標就是 REST 的前兩個把手
            if (Str(p, "type")?.StartsWith("GRADIENT_", StringComparison.Ordinal) == true && Affine.Of(p["transform"]).Inverse() is { } inv)
            {
                var (sx, sy) = inv.Apply(0, 0.5);
                var (ex, ey) = inv.Apply(1, 0.5);
                var (wx, wy) = inv.Apply(0, 1);
                paint["gradientHandlePositions"] = new JsonArray(
                    new JsonObject { ["x"] = sx, ["y"] = sy }, new JsonObject { ["x"] = ex, ["y"] = ey }, new JsonObject { ["x"] = wx, ["y"] = wy });
            }
            result.Add(paint);
        }
        return result;
    }

    private static void Corners(JsonObject node, JsonObject rest)
    {
        // 「各角獨立」打開但四角一樣時,Figma 自己也當成單一圓角(REST 給 cornerRadius)
        double[] corners = [Num(node, "rectangleTopLeftCornerRadius") ?? 0, Num(node, "rectangleTopRightCornerRadius") ?? 0,
            Num(node, "rectangleBottomRightCornerRadius") ?? 0, Num(node, "rectangleBottomLeftCornerRadius") ?? 0];
        if (node["rectangleCornerRadiiIndependent"]?.GetValue<bool>() == true && corners.Distinct().Count() > 1)
            rest["rectangleCornerRadii"] = new JsonArray([.. corners.Select(c => (JsonNode)c)]);
        else if (node["rectangleCornerRadiiIndependent"]?.GetValue<bool>() == true && corners[0] > 0)
            rest["cornerRadius"] = corners[0];
        else if (Num(node, "cornerRadius") is { } r && r > 0)
            rest["cornerRadius"] = r;
    }

    private static JsonArray Effects(JsonArray effects)
    {
        var result = new JsonArray();
        foreach (var e in effects.OfType<JsonObject>())
        {
            var effect = new JsonObject { ["type"] = Str(e, "type") };
            if (e["visible"] is JsonValue v && v.TryGetValue<bool>(out var visible) && !visible) effect["visible"] = false;
            if (e["color"] is JsonObject c) effect["color"] = c.DeepClone();
            if (e["offset"] is JsonObject o) effect["offset"] = new JsonObject { ["x"] = Num(o, "x") ?? 0, ["y"] = Num(o, "y") ?? 0 };
            effect["radius"] = Num(e, "radius") ?? 0;
            if (Num(e, "spread") is { } spread) effect["spread"] = spread;
            result.Add(effect);
        }
        return result;
    }

    /// <summary>auto-layout:.fig 叫 stack*,REST 叫 layoutMode / padding* / itemSpacing。沒開 auto-layout 的不寫(REST 也不寫)。</summary>
    private static void AutoLayout(JsonObject node, JsonObject rest)
    {
        if (Str(node, "stackPositioning") == "ABSOLUTE") rest["layoutPositioning"] = "ABSOLUTE";
        if (Str(node, "stackMode") is not ("HORIZONTAL" or "VERTICAL") || Str(node, "stackMode") is not { } mode) return;
        rest["layoutMode"] = mode;
        rest["itemSpacing"] = Num(node, "stackSpacing") ?? 0;
        var left = Num(node, "stackHorizontalPadding") ?? 0;
        var top = Num(node, "stackVerticalPadding") ?? 0;
        rest["paddingLeft"] = left;
        rest["paddingTop"] = top;
        // 沒存的右/下內距是 0,不是跟左/上一樣(真檔校準:左 64、右沒存的表單,Figma 顯示右 0)
        rest["paddingRight"] = Num(node, "stackPaddingRight") ?? 0;
        rest["paddingBottom"] = Num(node, "stackPaddingBottom") ?? 0;
        // .fig 把「space between」叫 SPACE_EVENLY;REST 與外掛 API 都叫 SPACE_BETWEEN
        if (Str(node, "stackPrimaryAlignItems") is { } primary) rest["primaryAxisAlignItems"] = primary == "SPACE_EVENLY" ? "SPACE_BETWEEN" : primary;
        if (Str(node, "stackCounterAlignItems") is { } counter) rest["counterAxisAlignItems"] = counter;
        if (Str(node, "stackWrap") == "WRAP") rest["layoutWrap"] = "WRAP";
    }

    /// <summary>文字:REST 的 style 物件。字重 .fig 只有字型樣式的名字("Semi Bold"),換成數字。</summary>
    private static void Text(JsonObject node, JsonObject rest)
    {
        rest["characters"] = Str(node["textData"], "characters") ?? "";
        var fontSize = Num(node, "fontSize");
        var styleName = Str(node["fontName"], "style") ?? "Regular";
        var style = new JsonObject
        {
            ["fontFamily"] = Str(node["fontName"], "family"),
            ["fontStyle"] = styleName,
            ["fontWeight"] = WeightOf(styleName),
            ["fontSize"] = fontSize,
        };
        if (styleName.Contains("italic", StringComparison.OrdinalIgnoreCase)) style["italic"] = true;
        if (LengthOf(node["lineHeight"], fontSize, autoIsNull: true) is { } lh) style["lineHeightPx"] = lh;
        if (LengthOf(node["letterSpacing"], fontSize, autoIsNull: false) is { } ls) style["letterSpacing"] = ls;
        foreach (var (from, to) in new[] { ("textAlignHorizontal", "textAlignHorizontal"), ("textCase", "textCase"),
                     ("textDecoration", "textDecoration"), ("textAutoResize", "textAutoResize") })
            if (Str(node, from) is { } v && v is not ("ORIGINAL" or "NONE")) style[to] = v;
        rest["style"] = style;
    }

    /// <summary>
    /// 行高、字距:.fig 存 { value, units }。PIXELS 就是 px;PERCENT 是字級的百分比;RAW 是字級的倍數。
    /// 行高 100%(Figma 的「Auto」)交給 null:REST 回的是字型實際的行高,這裡算不出來——寧可不比,也不報假的落差。
    /// </summary>
    private static double? LengthOf(JsonNode? length, double? fontSize, bool autoIsNull)
    {
        if (Num(length, "value") is not { } value) return null;
        return Str(length, "units") switch
        {
            "PIXELS" => value,
            "PERCENT" when autoIsNull && Math.Abs(value - 100) < 1e-6 => null,
            "PERCENT" => fontSize is { } f ? Math.Round(f * value / 100, 4) : null,
            "RAW" => fontSize is { } f ? Math.Round(f * value, 4) : null,
            _ => null,
        };
    }

    /// <summary>字型樣式名稱 → 字重(CSS 的對應)。看不出來的當 400。</summary>
    internal static double WeightOf(string style)
    {
        var s = style.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLower(CultureInfo.InvariantCulture);
        if (s.Contains("thin") || s.Contains("hairline")) return 100;
        if (s.Contains("extralight") || s.Contains("ultralight")) return 200;
        if (s.Contains("light")) return 300;
        if (s.Contains("semibold") || s.Contains("demibold")) return 600;
        if (s.Contains("extrabold") || s.Contains("ultrabold")) return 800;
        if (s.Contains("heavy")) return 800; // 真檔校準:Figma 把 Avenir Heavy 算成 800(Heavy 多半排在 Black 之前)
        if (s.Contains("black")) return 900;
        if (s.Contains("bold")) return 700;
        if (s.Contains("medium")) return 500;
        return 400;
    }
}
