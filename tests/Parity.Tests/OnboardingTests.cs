using Parity.Cli;
using Parity.Engine.DesignSources;
using Parity.Engine.DesignSources.Json;

namespace Parity.Tests;

/// <summary>
/// onboarding 的兩個接住點(DX 實查 D1/D6):init 範本要能被解析(含註解的雙路徑版),
/// 走 snapshot 卻沿用範本 frame "10:2" 的新手,錯誤訊息要說「該填什麼」。
/// </summary>
public class OnboardingTests
{
    [Fact]
    public void Init_template_with_comments_still_parses()
    {
        // 範本用註解並列 Figma / snapshot 兩條路——註解不能讓設定檔炸掉
        var path = Path.Combine(Path.GetTempPath(), $"parity-init-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, InitCommand.Template);
            var config = ParityConfig.Load(path);

            Assert.Equal("your Figma file key", config.FigmaFileKey);
            Assert.Null(config.DesignFile); // snapshot 路徑是註解掉的替代寫法,預設不生效
            var t = Assert.Single(config.Targets);
            Assert.Equal("10:2", t.Frame);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Missing_frame_error_names_the_snapshot_route_convention()
    {
        // 必撞組合:init 範本的 frame "10:2" × snapshot 基準(frame id 其實是 route)
        var path = Path.Combine(Path.GetTempPath(), $"parity-frame-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path,
                """{"id":"/","name":"snapshot","type":"Frame","box":{"x":0,"y":0,"width":800,"height":600}}""");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new JsonDesignSource().GetFrameAsync(new DesignRef(path, "10:2")));

            Assert.Contains("10:2", ex.Message);
            Assert.Contains("\"/\"", ex.Message);   // 有列出實際存在的 id
            Assert.Contains("route", ex.Message);   // 有講 snapshot 的慣例:frame id = route
        }
        finally
        {
            File.Delete(path);
        }
    }
}
