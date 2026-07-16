using Parity.Engine.DesignSources;
using Parity.Engine.ImplementationSources;
using Parity.Engine.Model;

namespace Parity.Tests;

/// <summary>測試用的節點工廠,讓測試讀起來像場景描述。</summary>
internal static class TestData
{
    public static DesignNode Design(
        string id, string name, DesignNodeType type = DesignNodeType.Frame,
        Box box = default, Rgba? fill = null, Typography? text = null,
        Insets? padding = null, double? itemSpacing = null, string? characters = null,
        string? layoutMode = null, string? sizingH = null, string? sizingV = null,
        params DesignNode[] children)
        => new(id, name, type, box, fill, text, padding, itemSpacing, null, children)
        {
            Characters = characters, LayoutMode = layoutMode,
            LayoutSizingHorizontal = sizingH, LayoutSizingVertical = sizingV,
        };

    public static RenderedNode Rendered(
        string selector, string tag = "div", string? text = null,
        Box box = default, Rgba? color = null, Rgba? background = null,
        Typography? typography = null, Insets padding = default,
        string? explicitMatch = null, string? domId = null, string? classes = null,
        string? ariaLabel = null, params RenderedNode[] children)
        => new(selector, tag, text, box, color, background, typography, padding, children)
        { ExplicitMatch = explicitMatch, DomId = domId, Classes = classes, AriaLabel = ariaLabel };
}
