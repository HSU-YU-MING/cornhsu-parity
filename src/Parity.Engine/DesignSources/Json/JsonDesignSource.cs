using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parity.Engine.DesignSources.Json;

/// <summary>
/// 本機 JSON 設計來源:直接讀一份 DesignNode 樹的 JSON 檔。
/// 用途:(1) 離線測試/示範,不需要 Figma token;(2) 提前驗證 IDesignSource 這扇門的可插拔性
/// (規畫書 M5 的 ImageDesignSource 走同一條路)。DesignRef.Source = JSON 檔路徑。
/// </summary>
public sealed class JsonDesignSource : IDesignSource
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
        // snapshot 凍結的真實網站 DOM 常見 30+ 層巢狀(每層 Children 佔 2 個 JSON 深度),
        // 預設 MaxDepth 64 讀不回來。與擷取端 WebImplementationSource.CaptureParseOptions 同值。
        MaxDepth = 512,
    };

    public async Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default)
    {
        var path = reference.Source;
        if (!File.Exists(path))
            throw new FileNotFoundException($"design JSON file not found: {path}", path);

        var json = await File.ReadAllTextAsync(path, ct);
        DesignNode root;
        try
        {
            // 兩種頂層形狀(1.0 審查面 6):新式信封 { "schemaVersion": 1, "root": {...} },
            // 或裸 DesignNode 樹(舊 snapshot 與手寫 design JSON)——沒有版本欄就視為 v1 照吃。
            var probe = JsonSerializer.Deserialize<SnapshotEnvelopeProbe>(json, SerializerOptions);
            if (probe?.SchemaVersion is { } version)
            {
                if (version > Snapshot.SnapshotDocument.CurrentSchemaVersion)
                    throw new InvalidOperationException(
                        $"design JSON {path} has schemaVersion {version}, which this Parity " +
                        $"(schemaVersion {Snapshot.SnapshotDocument.CurrentSchemaVersion}) does not understand — " +
                        "it was probably produced by a newer Parity. Upgrade the tool, or re-run `parity snapshot` with this version.");
                root = probe.Root
                    ?? throw new InvalidOperationException($"design JSON {path} has a schemaVersion but no \"root\" node.");
            }
            else
            {
                root = JsonSerializer.Deserialize<DesignNode>(json, SerializerOptions)
                    ?? throw new InvalidOperationException($"could not parse design JSON: {path}");
            }
        }
        catch (JsonException ex) when (ex.Message.Contains("depth", StringComparison.OrdinalIgnoreCase))
        {
            // 超深時 System.Text.Json 的訊息只講 JSON 深度,讀的人對不回自己的頁面——翻成人話
            throw new InvalidOperationException(
                $"design JSON nests deeper than the supported limit (JSON depth 512 ≈ 250 DOM levels): {path}. " +
                "If this is a snapshot, add the deepest region to \"ignore\" and re-run parity snapshot.", ex);
        }

        root = FillDefaults(root);

        // NodeId 為空 → 整棵樹;否則往下找指定節點
        if (string.IsNullOrEmpty(reference.NodeId) || reference.NodeId == root.Id)
            return root;

        // 新手最常見的組合:init 範本的 frame "10:2" + snapshot 基準(DX 實查 D6)——
        // 只說「找不到」不夠,要說該填什麼:snapshot 的 frame id 就是 target 的 route。
        return root.DescendantsAndSelf().FirstOrDefault(n => n.Id == reference.NodeId)
            ?? throw new InvalidOperationException(
                $"node {reference.NodeId} is not present in the design JSON: {path}. " +
                $"Available top-level ids: {string.Join(", ", root.DescendantsAndSelf().Take(4).Select(n => $"\"{n.Id}\""))}…" +
                " If this is a snapshot baseline, the frame id is the target's route (e.g. \"/\").");
    }

    /// <summary>JSON 可省略 children → 反序列化成 null,補回空清單。</summary>
    private static DesignNode FillDefaults(DesignNode node)
        => node with { Children = (node.Children ?? []).Select(FillDefaults).ToList() };

    /// <summary>
    /// 頂層形狀嗅探用:裸 DesignNode 也能安全反序列化進來(兩欄皆 null),
    /// 有 SchemaVersion 才走信封路徑。
    /// </summary>
    private sealed record SnapshotEnvelopeProbe(int? SchemaVersion, DesignNode? Root);
}
