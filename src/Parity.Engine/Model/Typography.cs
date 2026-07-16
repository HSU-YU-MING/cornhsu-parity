namespace Parity.Engine.Model;

/// <summary>字體度量。任一欄位為 null 代表該來源沒提供(就不比)。</summary>
public sealed record Typography(
    string? FontFamily = null,
    double? FontSize = null,
    double? FontWeight = null,
    double? LineHeight = null,
    double? LetterSpacing = null);
