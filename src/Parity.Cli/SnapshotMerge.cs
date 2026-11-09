using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Snapshot;

namespace Parity.Cli;

/// <summary>
/// <c>parity snapshot --target &lt;route&gt;</c>:只重拍一頁,併回既有快照,其他頁原封不動。
/// 2.5.0 以前它把整份快照換成那一頁——26 頁的基準變成 1 頁,其他 25 頁從此沒有基準
/// (作者網站 2026-11-09 實際踩到,靠 git 還原)。
/// </summary>
internal static class SnapshotMerge
{
    /// <summary>多頁快照的根 id(底下一頁一個 frame,frame id = route)。</summary>
    internal const string MultiRootId = "snapshot";

    internal static DesignNode MultiRoot(IReadOnlyList<DesignNode> frames)
        => new(MultiRootId, MultiRootId, DesignNodeType.Frame, default, null, null, null, null, null, frames.ToList());

    /// <param name="Root">合併後的根</param>
    /// <param name="StableIdAnchors">合併後的穩定 id 白名單(null = 不限制)</param>
    /// <param name="FrameIndex">這頁在多頁根底下的位置 = 參考截圖的編號(<c>x.{n}.png</c>);-1 = 單頁快照(<c>x.png</c>)</param>
    /// <param name="SingleBecameMulti">既有檔是別頁的單頁快照,合併後變成兩頁:它的 <c>x.png</c> 要改名成 <c>x.0.png</c></param>
    internal sealed record Result(DesignNode Root, IReadOnlyList<string>? StableIdAnchors, int FrameIndex, bool SingleBecameMulti);

    /// <summary>
    /// 把剛拍的一頁(<paramref name="frame"/>,id = route)併進既有快照(<paramref name="existing"/>,原樣讀入,未還原預設值)。
    /// 合不了就丟 <see cref="InvalidOperationException"/>,訊息講該怎麼做——寧可不寫,也不寫出一份語意混雜的基準。
    /// </summary>
    /// <param name="stableIds">這頁兩次載入都在的 id</param>
    /// <param name="pageHasRandomIds">這頁測到每次載入都變的 id(白名單才需要存)</param>
    internal static Result Merge(SnapshotDocument existing, DesignNode frame, IReadOnlyCollection<string> stableIds, bool pageHasRandomIds)
    {
        var root = existing.Root;

        // 既有檔就是這一頁的單頁快照:整份換掉,跟沒有 --target 時一樣,不涉及別頁
        if (root.Id == frame.Id)
            return new(frame, pageHasRandomIds ? stableIds.Order(StringComparer.Ordinal).ToList() : null, -1, false);

        EnsureMergeable(existing, frame.Id);

        // 白名單是整份快照共用的:check 擷取每一頁都只拿名單內的 id 當錨點。舊快照沒有名單(每頁都沒測到隨機 id),
        // 這頁現在有了,名單就得涵蓋所有頁面的穩定 id——其他頁沒有重拍,量不到
        if (existing.StableIdAnchors is null && pageHasRandomIds)
            throw new InvalidOperationException(
                $"{frame.Id} now has ids that change on every page load, so the snapshot needs a stable-id allowlist " +
                "covering every page — the other pages were not captured this time. Run `parity snapshot` without " +
                "--target once.");
        var anchors = existing.StableIdAnchors is { } old
            ? old.Union(stableIds, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
            : null;

        if (root.Id == MultiRootId)
        {
            var children = root.Children.ToList();
            var index = children.FindIndex(c => c.Id == frame.Id);
            if (index >= 0) children[index] = frame;
            else { index = children.Count; children.Add(frame); }
            return new(root with { Children = children }, anchors, index, false);
        }

        // 別頁的單頁快照:變成兩頁,原本那頁在前
        return new(MultiRoot([root, frame]), anchors, 1, true);
    }

    /// <summary>
    /// 開瀏覽器之前就能判斷的「合不了」(擷取要好幾秒,別讓人等完才被拒絕)。
    /// 既有檔就是這一頁的單頁快照時不用合併,永遠可以。
    /// </summary>
    internal static void EnsureMergeable(SnapshotDocument existing, string route)
    {
        if (existing.Root.Id == route) return;
        // 樣式欄位「沒寫」的意思要整份一致:舊快照的沒寫可能是「沒量過、不比」,新拍的這頁沒寫是「預設值」。
        // 信封只有一個標記,混在一起寫,舊頁面的「不比」會被讀成「預設值」,沒改的頁面憑空冒出落差
        if (existing.StyleDefaultsOmitted != true
            || (existing.StyleDefaultsVersion ?? 1) != SnapshotDocument.CurrentStyleDefaultsVersion)
            throw new InvalidOperationException(
                "the existing snapshot was written by an older Parity, which records page styles differently — " +
                "a page frozen now cannot be mixed into it. Run `parity snapshot` without --target once to " +
                "refreeze every page (compare the result before committing it).");
    }
}
