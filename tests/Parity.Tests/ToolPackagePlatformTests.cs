using System.Text.RegularExpressions;
using Parity.Engine.ImplementationSources.Web;

namespace Parity.Tests;

/// <summary>
/// NuGet 通路的「每個平台一包」:平台清單與發版順序都是設定檔裡的一行字,改錯了 pack 照樣綠,
/// 要到使用者在那個平台上安裝(或下一次發版)才炸。實際的包內容由 scripts/verify-tool-packages.py 看,
/// 真的安裝由 ci.yml 的 install-smoke* 跑;這裡守的是這些東西彼此之間對得上。
/// </summary>
public class ToolPackagePlatformTests
{
    private static string Read(params string[] parts) => Repo.Read(parts).Replace("\r\n", "\n");

    private static string Csproj => Read("src", "Parity.Cli", "Parity.Cli.csproj");

    private static string Release => Read(".github", "workflows", "release.yml");

    private static string[] CsprojRids()
    {
        var m = Regex.Match(Csproj, "<RuntimeIdentifiers>(?<rids>[^<]+)</RuntimeIdentifiers>");
        Assert.True(m.Success, "Parity.Cli.csproj 找不到 RuntimeIdentifiers");
        return m.Groups["rids"].Value.Split(';');
    }

    /// <summary>verify-tool-packages.py 的 EXPECTED_RIDS:指標包應該列出的平台。</summary>
    private static string[] CheckerRids()
    {
        var m = Regex.Match(Read("scripts", "verify-tool-packages.py"), @"^EXPECTED_RIDS = \[(?<body>[^\]]+)\]", RegexOptions.Multiline);
        Assert.True(m.Success, "verify-tool-packages.py 找不到 EXPECTED_RIDS");
        return [.. Regex.Matches(m.Groups["body"].Value, @"""(?<rid>[^""]+)""").Select(r => r.Groups["rid"].Value)];
    }

    /// <summary>verify-tool-packages.py 的 NODE_DIR:帶 Playwright driver、開得了瀏覽器的平台。</summary>
    private static string[] PlatformsWithABrowser()
    {
        var m = Regex.Match(Read("scripts", "verify-tool-packages.py"), @"^NODE_DIR = \{(?<body>[^}]+)\}", RegexOptions.Multiline);
        Assert.True(m.Success, "verify-tool-packages.py 找不到 NODE_DIR");
        return [.. Regex.Matches(m.Groups["body"].Value, @"""(?<rid>[^""]+)"":").Select(r => r.Groups["rid"].Value)];
    }

    [Fact]
    public void Csproj_and_the_package_checker_agree_on_the_platform_list()
    {
        Assert.Equal(CsprojRids().Order(StringComparer.Ordinal), CheckerRids().Order(StringComparer.Ordinal));
        // 兩個屬性要一致:只設 ToolPackageRuntimeIdentifiers 的話,內層 pack 會 NETSDK1047
        Assert.Contains("<ToolPackageRuntimeIdentifiers>$(RuntimeIdentifiers)</ToolPackageRuntimeIdentifiers>", Csproj);
    }

    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    [InlineData("linux-musl-x64")]
    [InlineData("linux-musl-arm64")]
    [InlineData("any")]
    public void Platforms_people_install_from_today_stay_on_the_list(string rid)
    {
        // 清單可以加、不能減:拿掉一個平台,那個平台的使用者就裝不起來。any 是其他所有平台的後備
        Assert.Contains(rid, CsprojRids());
    }

    [Fact]
    public void Every_npm_platform_has_a_nuget_package_with_a_browser()
    {
        // 兩個通路 README 都說功能相同:npm 支援的平台,NuGet 那邊不能少了瀏覽器
        var npm = Regex.Matches(Read("npm", "prepare.mjs"), @"\{ rid: ""(?<rid>[^""]+)""").Select(m => m.Groups["rid"].Value).ToList();
        Assert.Equal(6, npm.Count);
        Assert.Empty(npm.Except(PlatformsWithABrowser()));
    }

    [Fact]
    public void Ci_installs_on_a_real_machine_for_every_platform_with_a_browser()
    {
        var job = Regex.Match(Read(".github", "workflows", "ci.yml"), @"^  install-smoke:\n(?<body>(?:    .*\n|\n)*)", RegexOptions.Multiline);
        Assert.True(job.Success, "ci.yml 找不到 install-smoke job");

        var machines = Regex.Matches(job.Groups["body"].Value, @"\{ os: [^,]+, rid: (?<rid>[a-z0-9-]+) \}").Select(m => m.Groups["rid"].Value);
        Assert.Equal(PlatformsWithABrowser().Order(StringComparer.Ordinal), machines.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Platform_packages_depend_on_the_users_dotnet_and_let_the_sdk_make_the_launcher()
    {
        // UseAppHost=false → Runner="dotnet"。少了它,Windows 上的啟動檔是 parity.cmd,bash(action.yml)叫不到 parity
        Assert.Contains("<UseAppHost>false</UseAppHost>", Csproj);
        // 自帶 runtime:每包多約 50 MB,.NET 的安全修補要等 Parity 重發
        Assert.Contains("<SelfContained>false</SelfContained>", Csproj);
        // 裁剪:check 一讀設定檔就崩潰
        Assert.DoesNotContain("PublishTrimmed", Regex.Replace(Csproj, "<!--.*?-->", "", RegexOptions.Singleline));
    }

    [Fact]
    public void Each_package_decides_which_node_it_carries_not_the_engine()
    {
        // 引擎沒設的話,Playwright 的 targets 帶「建置機器平台」的 Node,經由 ProjectReference 流進每個平台包(+45 MB)
        Assert.Matches(@"<PlaywrightPlatform Condition=""'\$\(PlaywrightPlatform\)' == ''"">none</PlaywrightPlatform>",
            Read("src", "Parity.Engine", "Parity.Engine.csproj"));
        // win-arm64 與沒有 Node 的平台不能直接用 RID:Playwright 不認得的值會落回全收,又回到 240 MB
        Assert.Contains("""<PlaywrightPlatform Condition="'$(RuntimeIdentifier)' == 'win-arm64'">win</PlaywrightPlatform>""", Csproj);
        Assert.Contains("""<PlaywrightPlatform Condition="'$(RuntimeIdentifier)' == 'any' or $(RuntimeIdentifier.StartsWith('linux-musl-'))">none</PlaywrightPlatform>""", Csproj);
    }

    [Fact]
    public void Release_checks_the_packages_then_pushes_platforms_before_the_pointer()
    {
        // 指標包先上架而平台包還沒好的那段時間,所有人的 dotnet tool install(含 Action 的 "2.*")都會失敗
        var positions = new[]
        {
            "verify-tool-packages.py ./artifacts",
            "push-tool-packages.sh platform",
            "push-tool-packages.sh wait",
            "id: login_pointer",
            "push-tool-packages.sh pointer",
        }.Select(step => (step, at: Release.IndexOf(step, StringComparison.Ordinal))).ToList();

        Assert.All(positions, p => Assert.True(p.at >= 0, $"release.yml 找不到 {p.step}"));
        Assert.Equal(positions.Select(p => p.at).Order(), positions.Select(p => p.at));
    }

    [Fact]
    public void Release_never_pushes_everything_at_once_or_packs_without_building()
    {
        // `dotnet nuget push *.nupkg` 不保證順序,也不等 nuget.org 查得到平台包
        Assert.DoesNotContain("dotnet nuget push", Release);
        // 平台包要各自以 RuntimeIdentifier 建置,--no-build 會 MSB3030。只合併 csproj 而沒改這行,下一次發版會直接紅
        var pack = Regex.Match(Release, @"run: dotnet pack (?<args>.*)");
        Assert.True(pack.Success, "release.yml 找不到 dotnet pack");
        Assert.DoesNotContain("--no-build", pack.Groups["args"].Value);
    }

    [Fact]
    public void Pointer_push_gets_a_fresh_key_because_the_first_one_can_expire_while_waiting()
    {
        // NuGet 的臨時金鑰只活 1 小時,等平台包驗證可能超過;少了第二次登入,指標包會推不上去
        Assert.Equal(2, Regex.Matches(Release, @"uses: NuGet/login@").Count);
        Assert.Contains("NUGET_API_KEY: ${{ steps.login_pointer.outputs.NUGET_API_KEY }}", Release);
    }

    [Fact]
    public void No_driver_message_names_exactly_the_platforms_with_a_browser()
    {
        var withBrowser = PlatformsWithABrowser();
        Assert.All(withBrowser, rid => Assert.Contains(rid, WebImplementationSource.NoDriverMessage));
        Assert.All(CheckerRids().Except(withBrowser).Except(["any"]), rid => Assert.DoesNotContain(rid, WebImplementationSource.NoDriverMessage));
    }

    [Fact]
    public void Install_browser_looks_for_the_driver_script_not_the_node_folder()
    {
        // npm 通路拿掉了 Node、改用使用者現成的,driver 的 JS 還在,不能被當成沒有 driver
        var dir = Directory.CreateTempSubdirectory("parity-driver-");
        try
        {
            Assert.False(InstallBrowserCommand.HasBundledDriver(dir.FullName));

            var package = Directory.CreateDirectory(Path.Combine(dir.FullName, ".playwright", "package"));
            File.WriteAllText(Path.Combine(package.FullName, "cli.js"), "");
            Assert.True(InstallBrowserCommand.HasBundledDriver(dir.FullName));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_normal_build_has_the_driver_install_browser_looks_for()
    {
        Assert.True(InstallBrowserCommand.HasBundledDriver(AppContext.BaseDirectory),
            "測試專案的輸出應該有 .playwright/package/cli.js;沒有的話 install-browser 會對所有人說平台不支援");
    }
}
