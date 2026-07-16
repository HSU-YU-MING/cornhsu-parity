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
    };

    public async Task<DesignNode> GetFrameAsync(DesignRef reference, CancellationToken ct = default)
    {
        var path = reference.Source;
        if (!File.Exists(path))
            throw new FileNotFoundException($"找不到設計 JSON 檔:{path}", path);

        await using var stream = File.OpenRead(path);
        var root = await JsonSerializer.DeserializeAsync<DesignNode>(stream, SerializerOptions, ct)
            ?? throw new InvalidOperationException($"設計 JSON 解析失敗:{path}");

        root = FillDefaults(root);

        // NodeId 為空 → 整棵樹;否則往下找指定節點
        if (string.IsNullOrEmpty(reference.NodeId) || reference.NodeId == root.Id)
            return root;

        return root.DescendantsAndSelf().FirstOrDefault(n => n.Id == reference.NodeId)
            ?? throw new InvalidOperationException($"設計 JSON 裡找不到節點 {reference.NodeId}:{path}");
    }

    /// <summary>JSON 可省略 children → 反序列化成 null,補回空清單。</summary>
    private static DesignNode FillDefaults(DesignNode node)
        => node with { Children = (node.Children ?? []).Select(FillDefaults).ToList() };
}
