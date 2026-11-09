using System.Globalization;
using System.Text;
using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;

namespace Parity.Engine.Comparison;

public sealed record NodePair(DesignNode Design, RenderedNode Rendered, string MatchedBy);

public sealed record MatchResult(
    IReadOnlyList<NodePair> Pairs,
    IReadOnlyList<UnmatchedNode> Unmatched);

/// <summary>
/// 節點配對(規畫書 4.7)——以設計端為錨,自動優先、只補漏:
///   1. 手動錨點(map 檔或 data-parity,擷取端已標成 ExplicitMatch)→ 最高優先
///   2. 文字錨點:TEXT 節點文字 ↔ 頁面元素文字(唯一才配;多個同文字時用圖層名消歧,仍不硬湊)
///   3. 圖層名 ↔ DOM id / class / aria-label / data-testid(唯一才配;多個同名時看它在哪一區、裡面有什麼,仍分不出就 ambiguous-name)
///   4. 容器推論:配不到的容器,用「已配對子孫的最近共同祖先(LCA)」反推——純結構、不猜
/// 配不到 → 誠實進未配對清單,不假裝全對上。文字節點的文字整頁都找不到(擷取端有給整頁文字時才判斷)
/// → text-not-found:元件不見或文字被改掉,而不是「有但分不出來」。
/// </summary>
public static class Matcher
{
    /// <summary>
    /// 這一版寫得出來的所有未配對原因(report.json 的 <c>unmatched[].reason</c>;報告讀者那邊是開放集合)。
    /// 設定檔的 <c>gate.failOnUnmatched</c> 對著這張表驗拼字——拼錯的原因永遠對不到,gate 會靜靜失效。
    /// 新增原因時 ReportVocabularyTests 會逼兩份 README 的字彙表一起改。
    /// </summary>
    public static IReadOnlyList<string> UnmatchedReasons { get; } =
        ["no-anchor", "ambiguous-or-missing-text", "text-not-found", "randomized-id", "size-implausible", "ambiguous-name"];

    public static MatchResult Match(DesignNode designRoot, RenderedNode renderedRoot)
    {
        // 設計端為錨:報告長度 = 設計節點數,有界又有意義(不反向掃整頁;
        // 「設計稿沒有的元素」是另一份選用的清單,在配對之後由 ExtrasFinder 列出,不進配對與比對)
        var designNodes = designRoot.DescendantsAndSelf().Skip(1).ToList(); // 跳過 root frame 本身
        var candidates = renderedRoot.DescendantsAndSelf().ToList();

        var pairs = new List<NodePair>();
        var unmatched = new List<UnmatchedNode>();
        var taken = new HashSet<RenderedNode>(ReferenceEqualityComparer.Instance);
        // 設計節點 Id → 已配到的實作節點,供第 4 關算 LCA
        var matchedByDesign = new Dictionary<string, RenderedNode>();

        void Pair(DesignNode d, RenderedNode r, string by)
        {
            taken.Add(r);
            matchedByDesign[d.Id] = r;
            pairs.Add(new NodePair(d, r, by));
        }

        // --- 第 0 關:selector 身分(snapshot 設計來源:設計節點 Id 就是擷取時的 CSS selector)---
        // 100% 確定性——同一個 selector 就是同一個元素。Figma id("10:2")長得不像 selector,不會誤中。
        var bySelector = new Dictionary<string, RenderedNode>();
        foreach (var r in candidates)
            bySelector.TryAdd(r.Selector, r);

        // --- 第 1 關:手動錨點(ExplicitMatch = data-parity 或 map 檔),對圖層名 ---
        var explicitByName = new Dictionary<string, RenderedNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in candidates.Where(r => !string.IsNullOrEmpty(r.ExplicitMatch)))
            explicitByName.TryAdd(r.ExplicitMatch!, r);

        var remaining = new List<DesignNode>();
        foreach (var d in designNodes)
        {
            if (bySelector.TryGetValue(d.Id, out var same) && !taken.Contains(same))
                Pair(d, same, "selector");
            else if (explicitByName.TryGetValue(d.Name, out var hit) && !taken.Contains(hit))
                Pair(d, hit, "explicit");
            else
                remaining.Add(d);
        }

        // 整頁文字(擷取端有提供才有;假來源、舊測試是 null → 下面的原因完全照舊)。
        // byText 只收元素自己的文字(Hello <b>world</b> 會被拆開),不能拿它判斷「找不到」;
        // 整頁文字涵蓋跨元素斷開的字、shadow DOM、同源 iframe、表單值、::before/::after(CaptureScript)。
        // 比法比 byText 寬(見 TextFingerprint),再用不分大小寫的子字串——只有「整頁確定沒有這段文字」才是 text-not-found,
        // 有多個同文字分不出來、或文字在但被拆開,都維持 ambiguous-or-missing-text。
        var pageText = renderedRoot.PageText is { } raw ? TextFingerprint(raw) : null;
        bool TextNotFound(DesignNode d)
            => pageText is not null
               && !string.IsNullOrWhiteSpace(d.Characters)
               && !pageText.Contains(TextFingerprint(d.Characters!), StringComparison.OrdinalIgnoreCase);

        // --- 第 2 關:文字錨點(主力,不用人做任何事)---
        var byText = candidates
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .GroupBy(r => NormalizeText(r.Text!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // 放寬的文字索引,只在精確比對完全對不到時才用:設計稿手動斷行的中文壓空白後中間多一個空格,頁面上是連著的,
        // 精確比對永遠對不上,節點進未配對清單,屬性一個都沒比到。下面照樣要唯一才配,不會因為放寬就猜
        var byFingerprint = candidates
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .GroupBy(r => TextFingerprint(r.Text!), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // 再放寬一層:同一段文字被行內元素拆開(歡迎使用<b>我們的</b>服務、請先<b>登入</b><br>帳號)。
        // 元素自己的文字湊不出來,要看連子孫一起的文字。區塊容器不算(TextIsInline):它的字體是別的元素的,配上去就是假落差
        var bySplitText = candidates
            .Where(r => r.TextIsInline && !string.IsNullOrWhiteSpace(r.TextContent) && r.TextContent!.Length < DiffEngine.TextContentLimit)
            .GroupBy(r => TextFingerprint(r.TextContent!), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var stillRemaining = new List<DesignNode>();
        foreach (var d in remaining)
        {
            var split = false;
            if (!string.IsNullOrWhiteSpace(d.Characters)
                && (byText.TryGetValue(NormalizeText(d.Characters!), out var hits)
                    || byFingerprint.TryGetValue(TextFingerprint(d.Characters!), out hits)
                    || (split = bySplitText.TryGetValue(TextFingerprint(d.Characters!), out hits))))
            {
                var free = hits!.Where(h => !taken.Contains(h)).ToList();
                // <p><span>Hello <b>world</b></span></p>:p 和 span 的文字一樣,是同一段,取最裡面的
                if (split) free = free.Where(h => !free.Any(o => !ReferenceEquals(o, h) && h.DescendantsAndSelf().Skip(1).Contains(o))).ToList();
                if (free.Count == 1) // 唯一才配
                {
                    Pair(d, free[0], "auto-text");
                    continue;
                }
                // 多個同文字:若剛好一個候選的 id/class/aria 對得上圖層名 → 用它消歧
                // (高精準;沒有明確線索就不猜位置,避免配錯 → 產生假落差)
                var key = NormalizeName(d.Name);
                if (free.Count > 1 && key.Length > 0)
                {
                    var named = free.Where(h => NameMatches(h, key)).ToList();
                    if (named.Count == 1)
                    {
                        Pair(d, named[0], "auto-text");
                        continue;
                    }
                }
            }
            stillRemaining.Add(d);
        }

        // --- 第 3 關:圖層名 ↔ id / class / aria-label(附尺寸合理性防線)---
        // 唯一才配,跟文字錨點同一個原則(2.5.0)。以前同名的元素有好幾個時拿文件順序第一個尺寸合理的:
        // 評估語料裡名字配對只對 72%,基準頁的假落差 92.9% 來自這裡(配錯一個元素,它的每個屬性都報一條)。
        // 好幾個時先看它在哪一區、裡面有什麼(ByContext),還分不出來就列 ambiguous-name,不猜。
        var chainOf = BuildAncestorChains(renderedRoot);
        var parentOf = new Dictionary<DesignNode, DesignNode>(ReferenceEqualityComparer.Instance);
        foreach (var n in designRoot.DescendantsAndSelf())
            foreach (var c in n.Children ?? [])
                parentOf[c] = n;

        // 沒有任何同名元素可用時的原因(跟 2.4 以前一樣)
        string NoNameHit(DesignNode d)
            => string.IsNullOrWhiteSpace(d.Characters) ? "no-anchor"
                : TextNotFound(d) ? "text-not-found" : "ambiguous-or-missing-text";

        var pending = new List<(DesignNode Node, string Reason)>();
        var tied = new List<(DesignNode Node, List<RenderedNode> Options)>();
        foreach (var d in stillRemaining)
        {
            var key = NormalizeName(d.Name);
            if (key.Length == 0)
            {
                pending.Add((d, TextNotFound(d) ? "text-not-found" : "no-anchor"));
                continue;
            }

            var named = candidates.Where(r => !taken.Contains(r) && NameMatches(r, key)).ToList();
            var plausible = named.Where(r => SizePlausible(d, r)).ToList();

            if (plausible.Count == 1)
                Pair(d, plausible[0], "auto-name");
            else if (plausible.Count > 1)
                tied.Add((d, plausible)); // 等全部唯一的都配完,區域與內容的線索才最多
            else if (named.Count > 0)
                pending.Add((d, "size-implausible"));
            else
                pending.Add((d, NoNameHit(d)));
        }

        // 一個解開了,可能成為別人的區域或內容線索,所以反覆到沒有進展為止。順序固定,結果也固定
        for (var progressedByContext = true; progressedByContext && tied.Count > 0;)
        {
            progressedByContext = false;
            for (var i = 0; i < tied.Count; i++)
            {
                var (d, options) = tied[i];
                var left = ByContext(d, options.Where(r => !taken.Contains(r)).ToList(), parentOf, matchedByDesign, chainOf);
                if (left.Count != 1) continue;
                Pair(d, left[0], "auto-name");
                tied.RemoveAt(i--);
                progressedByContext = true;
            }
        }
        foreach (var (d, options) in tied)
            pending.Add((d,
                options.All(taken.Contains) ? NoNameHit(d)       // 同名的都被別人配走了
                : TextNotFound(d) ? "text-not-found"            // 文字整頁都沒有:元件不見,比「分不出來」更有用
                : "ambiguous-name"));

        // --- 第 3.5 關:框 + 文字,網頁上是同一個元素(2.5.0)---
        // 設計稿常把按鈕畫成一個框包一段字,網頁上只有一個 <button>:字在第 2 關配上之後,框沒有元素可配,
        // 背景色、內距、圓角一個都比不到(評估語料模擬 Figma 的條件下,這類框 827 個只配上 10 個)。
        // 框裡只有這一段字時,從字配到的元素往外找大小對得上框的那一層(TextFrameElement)。
        // 挑到元素本身 → auto-shared:兩個設計節點共用一個元素,框比框的屬性、字比字的屬性(字不比尺寸與位置,不會重複報)。
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var frame = pending[i].Node;
            if (frame.Type == DesignNodeType.Text || frame.Children is not [{ Type: DesignNodeType.Text } text]) continue;
            if (!matchedByDesign.TryGetValue(text.Id, out var element)) continue;
            if (TextFrameElement(frame, element, chainOf, taken) is not { } pick) continue;

            Pair(frame, pick, ReferenceEquals(pick, element) ? "auto-shared" : "auto-container");
            pending.RemoveAt(i);
        }

        // --- 第 4 關:容器推論——已配到 ≥2 個子孫的容器,對應到那些子孫的最近共同祖先 ---
        // 純結構推論(不比對外觀、不猜),把「無文字/名字對不上」但內容配對得上的容器救回來。
        bool progressed = true;
        while (progressed)
        {
            progressed = false;
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var d = pending[i].Node;
                if (d.Children is null || d.Children.Count == 0) continue;

                var matchedDesc = d.DescendantsAndSelf().Skip(1)
                    .Select(c => matchedByDesign.TryGetValue(c.Id, out var rn) ? rn : null)
                    .Where(rn => rn is not null).Select(rn => rn!).ToList();
                if (matchedDesc.Count < 2) continue;

                var lca = LowestCommonAncestor(matchedDesc, chainOf);
                if (lca is null || ReferenceEquals(lca, renderedRoot) || taken.Contains(lca)) continue;

                Pair(d, lca, "auto-container");
                pending.RemoveAt(i);
                progressed = true;
            }
        }

        foreach (var (node, reason) in pending)
            unmatched.Add(new UnmatchedNode(node.Name, node.Id,
                reason == "no-anchor" && ContainsRandomizedToken(node.Id) ? "randomized-id" : reason,
                node.Box));

        return new MatchResult(pairs, unmatched);
    }

    /// <summary>
    /// snapshot 模式的已知盲點(野生實查 F2):React useId / CSS-in-JS 之類的隨機 id 每次載入
    /// 重新生成,凍在 snapshot 裡的 selector 在下次載入的頁面上不存在,selector 身分關必落空。
    /// 這裡不救配對(改寫 selector 會讓既有 snapshot 全數失效),只把原因講清楚:
    /// 「randomized-id」而不是籠統的「no-anchor」,使用者才知道該 ignore 掉或改用 data-parity。
    /// 樣態:selector 裡有長度 ≥8、字母數字混雜的高熵 token,例如 label-92g58lqqado、
    /// #3jrOqJD10fXAt6AjqpbEG。純字母的隨機字串(rkxvdnnzty)分不出來,誠實漏放——
    /// 這是提示用啟發法,寧可漏標也不把穩定 id 誤標成隨機。Figma id("10:2")不會誤中。
    /// 啟發法與 CaptureScript 的 looksRandom 同步(那邊擷取時就不拿高熵 id 當錨點,
    /// 新 snapshot 的 selector 不會再含隨機 id;這裡只剩舊 snapshot 的遷移提示)——兩邊改要一起改。
    /// </summary>
    internal static bool ContainsRandomizedToken(string selector)
    {
        foreach (var token in selector.Split(SelectorSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 8) continue;
            int digits = 0, letters = 0;
            foreach (var ch in token)
            {
                if (char.IsAsciiDigit(ch)) digits++;
                else if (char.IsAsciiLetter(ch)) letters++;
            }
            if (digits + letters != token.Length) continue; // 只看純字母數字 token
            if (digits >= 2 && letters >= 3) return true;
            // 長 token + 內部數字(頭尾是字母)也算:wtz1gbigyt、bpi6prqmqwr——
            // 真實命名的數字幾乎都在字尾(html5、col2),內嵌數字的長字串多半是雜湊
            if (digits >= 1 && token.Length >= 10
                && char.IsAsciiLetter(token[0]) && char.IsAsciiLetter(token[^1])) return true;
        }
        return false;
    }

    private static readonly char[] SelectorSeparators =
        [' ', '>', '.', '#', ':', '(', ')', '[', ']', '=', '"', '\'', '-', '_', ','];

    /// <summary>
    /// 同名候選有好幾個時,用已經配好的節點縮小範圍(第 3 關),只用結構,不看座標:
    /// 1. 哪一區:最近一個已配對的設計祖先 → 候選要在它配到的元素裡面(三張卡片裡各有一個 icon)。
    /// 2. 裡面有什麼:已配對的設計子孫 → 留下包住最多個的候選(三張卡片的標題文字各不相同)。
    /// 回傳剩下的候選;剩一個才算解開。祖先配錯時可能篩掉正確的那個,這時通常剩 0 個或好幾個,照樣列 ambiguous-name。
    /// </summary>
    private static List<RenderedNode> ByContext(
        DesignNode d, List<RenderedNode> options,
        Dictionary<DesignNode, DesignNode> parentOf,
        Dictionary<string, RenderedNode> matchedByDesign,
        Dictionary<RenderedNode, IReadOnlyList<RenderedNode>> chainOf)
    {
        // x 在 container 裡面(不含自己):x 的祖先鏈上有 container
        bool Inside(RenderedNode x, RenderedNode container)
            => !ReferenceEquals(x, container) && chainOf[x].Contains(container, ReferenceEqualityComparer.Instance);

        RenderedNode? region = null;
        for (var p = parentOf.GetValueOrDefault(d); p is not null && region is null; p = parentOf.GetValueOrDefault(p))
            matchedByDesign.TryGetValue(p.Id, out region);
        if (region is not null)
            options = options.Where(r => Inside(r, region)).ToList();
        if (options.Count <= 1) return options;

        var contents = d.DescendantsAndSelf().Skip(1)
            .Select(c => matchedByDesign.GetValueOrDefault(c.Id))
            .OfType<RenderedNode>().ToList();
        if (contents.Count == 0) return options;

        var held = options.Select(r => (Node: r, Count: contents.Count(c => Inside(c, r)))).ToList();
        var most = held.Max(h => h.Count);
        return most == 0 ? options : held.Where(h => h.Count == most).Select(h => h.Node).ToList();
    }

    /// <summary>
    /// 只包一段字的框(第 3.5 關)該配哪個元素:字配到的元素本身,或外面一層層「只包著它」的 wrapper,挑大小最接近框的。
    /// 評估語料裡兩種情況分得很開:框和字真的是同一個元素時,元素跟框一樣大、跟字差一截(內距);
    /// 不是同一個元素時,元素跟字一樣大,框是外面大一圈的那層。所以要對得上框,不是對得上字。
    /// 不配的情況:沒有一層對得上(每一軸差超過 4px 或 10%);兩層一樣近(<c>&lt;li&gt;&lt;a&gt;</c> 一樣大),分不出來,不猜。
    /// wrapper 只往外走到「裡面有別的已配對節點」或「本身已經代表別的設計節點」為止,根不算。
    /// </summary>
    private static RenderedNode? TextFrameElement(
        DesignNode frame, RenderedNode element,
        Dictionary<RenderedNode, IReadOnlyList<RenderedNode>> chainOf, HashSet<RenderedNode> taken)
    {
        static double Slack(double size) => Math.Max(4, size * 0.1);
        bool Fits(Box b) => Math.Abs(b.W - frame.Box.W) <= Slack(frame.Box.W) && Math.Abs(b.H - frame.Box.H) <= Slack(frame.Box.H);
        double Distance(Box b) => Math.Abs(b.W - frame.Box.W) + Math.Abs(b.H - frame.Box.H);

        var options = new List<RenderedNode> { element };
        var chain = chainOf[element];
        for (var i = chain.Count - 2; i >= 1; i--) // chain[0] 是根,chain[^1] 是元素本身
        {
            var wrapper = chain[i];
            if (taken.Contains(wrapper) || HoldsOtherMatch(wrapper, chain[i + 1], taken)) break;
            options.Add(wrapper);
            // 往外只會更大:兩軸都已經大過框的容許範圍,再外面的層不可能對得上
            if (wrapper.Box.W > frame.Box.W + Slack(frame.Box.W) && wrapper.Box.H > frame.Box.H + Slack(frame.Box.H)) break;
        }

        var fitting = options.Where(o => Fits(o.Box)).OrderBy(o => Distance(o.Box)).ToList();
        if (fitting.Count == 0) return null;
        if (fitting.Count > 1 && Distance(fitting[1].Box) - Distance(fitting[0].Box) < 0.5) return null;

        // 往內也看一眼:字配到的元素裡面,只有一個子元素而且那一層也對得上框(一樣近或更近),代表字本身可能配到外層了
        // (評估語料實測:字配到 <li>,真正的元素是裡面一樣大的 <a>)。這時框跟著配上去只會把字的錯再放大成內距落差,不配
        var best = Distance(fitting[0].Box);
        for (var inner = element; inner.Children is [var only];)
        {
            inner = only;
            if (Fits(inner.Box) && Distance(inner.Box) < best + 0.5) return null;
        }
        return fitting[0];
    }

    /// <summary>wrapper 的子樹裡,除了 inner 那一支之外,有沒有已配對的節點。</summary>
    private static bool HoldsOtherMatch(RenderedNode wrapper, RenderedNode inner, HashSet<RenderedNode> taken)
    {
        foreach (var child in wrapper.Children ?? [])
        {
            if (ReferenceEquals(child, inner)) continue;
            if (child.DescendantsAndSelf().Any(taken.Contains)) return true;
        }
        return false;
    }

    /// <summary>每個實作節點 → 從 root 到自己的祖先鏈(含自己),供算 LCA。</summary>
    private static Dictionary<RenderedNode, IReadOnlyList<RenderedNode>> BuildAncestorChains(RenderedNode root)
    {
        var map = new Dictionary<RenderedNode, IReadOnlyList<RenderedNode>>(ReferenceEqualityComparer.Instance);
        void Walk(RenderedNode n, List<RenderedNode> prefix)
        {
            var chain = new List<RenderedNode>(prefix) { n };
            map[n] = chain;
            foreach (var c in n.Children ?? []) Walk(c, chain);
        }
        Walk(root, []);
        return map;
    }

    /// <summary>一組實作節點的最近共同祖先 = 各自祖先鏈的最長共同前綴的末端。</summary>
    private static RenderedNode? LowestCommonAncestor(
        List<RenderedNode> nodes, Dictionary<RenderedNode, IReadOnlyList<RenderedNode>> chainOf)
    {
        if (nodes.Count == 0) return null;
        var common = chainOf[nodes[0]];
        var len = common.Count;
        foreach (var n in nodes.Skip(1))
        {
            var chain = chainOf[n];
            var i = 0;
            var max = Math.Min(len, chain.Count);
            while (i < max && ReferenceEquals(common[i], chain[i])) i++;
            len = i;
        }
        return len > 0 ? common[len - 1] : null;
    }

    /// <summary>
    /// auto-name 的尺寸合理性防線(2026-08-11 路線 B 實查 B3):通用圖層名(Content/Button)
    /// 會在無關頁面撞到同名元素,產生 critical 級荒謬落差、傷害信任。實測數據:
    /// 假配對的面積比 148×~703×(Content 1969×3948 vs 603×41 = 318×),正當配對全部 ≈1×;
    /// 「設計畫 3 筆、實作長出 20 筆」的合法內容差也只到個位數——面積比 16(等於雙軸各 4×)
    /// 之上不可能是同一個東西,拒當配對。已知極限:同尺寸的撞名(16×16 圖示 vs 16×16 圖示)
    /// 幾何分不出來,誠實放行——那要語意才殺得掉。
    /// </summary>
    private static bool SizePlausible(DesignNode d, RenderedNode r)
    {
        var da = Math.Max(1, d.Box.W) * Math.Max(1, d.Box.H);
        var ra = Math.Max(1, r.Box.W) * Math.Max(1, r.Box.H);
        return Math.Max(da, ra) / Math.Min(da, ra) <= 16;
    }

    private static bool NameMatches(RenderedNode r, string normalizedLayerName)
    {
        if (r.DomId is not null && NormalizeName(r.DomId) == normalizedLayerName) return true;
        if (r.AriaLabel is not null && NormalizeName(r.AriaLabel) == normalizedLayerName) return true;
        if (r.Classes is not null)
            foreach (var cls in r.Classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (NormalizeName(cls) == normalizedLayerName) return true;
        return false;
    }

    /// <summary>文字正規化:壓空白、不分大小寫。</summary>
    internal static string NormalizeText(string s)
        => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// 放寬的文字比對鍵:拿掉所有空白與看不見的格式字元(軟連字號、零寬空白),彎引號當直引號。
    /// text-not-found 開了 gate.failOnUnmatched 就會擋 PR,所以寧可少標,不能誤標,下面這些文字畫面上都看得到:
    /// 設計稿手動斷行的中文(<c>歡迎使用\n我們的服務</c> 壓空白後中間多一個空格,頁面上是連著的)、
    /// 靠 margin 隔開的行內元素(innerText 是 <c>Price:$10</c>)、設計工具和網頁各用一種引號。
    /// 文案差異不是元件不見。各段之間的 \u001f 是控制字元,不會被拿掉,子字串仍然不會橫跨兩段。
    /// 配對只在精確比對完全對不到時才退回用它,而且一樣要唯一才配:配對要對到單一元素,一開始就放寬會多配。
    /// </summary>
    internal static string TextFingerprint(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            sb.Append(c switch
            {
                '‘' or '’' or '‚' or '‛' => '\'',
                '“' or '”' or '„' or '‟' => '"',
                _ => c,
            });
        }
        return sb.ToString();
    }

    /// <summary>名稱正規化:只留字母數字、小寫。"CTA Button" == "cta-button" == "ctaButton"。</summary>
    internal static string NormalizeName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
