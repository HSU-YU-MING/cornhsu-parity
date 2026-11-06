using System.Text.Json.Serialization;

namespace Parity.Engine.Model;

/// <summary>字體度量。任一欄位為 null 代表該來源沒提供(就不比)。</summary>
public sealed record Typography(
    string? FontFamily = null,
    double? FontSize = null,
    double? FontWeight = null,
    double? LineHeight = null,
    double? LetterSpacing = null)
{
    // 下面這些是 2.x 的選填加法,用 CSS 的字彙。沒有值就不寫出來,既有的快照才會逐位元相同(凍結面 6)

    /// <summary>"normal" / "italic"。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FontStyle { get; init; }

    /// <summary>"none" / "underline" / "line-through" / "line-through underline"。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextDecoration { get; init; }

    /// <summary>"none" / "uppercase" / "lowercase" / "capitalize" / "small-caps"。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextTransform { get; init; }

    /// <summary>"left" / "center" / "right" / "justify"。對齊沒有視覺意義時(文字框隨內容縮放)是 null。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextAlign { get; init; }
}
