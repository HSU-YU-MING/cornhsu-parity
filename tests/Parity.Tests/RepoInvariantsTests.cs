using System.Text.Json;
using System.Text.RegularExpressions;

namespace Parity.Tests;

/// <summary>
/// CLAUDE.md「地雷」與發版規則裡,不是程式邏輯、而是**設定檔的一行字**的那些不變式。
/// 它們的共同點:改錯了不會有任何錯誤訊息,要到使用者機器上(或下一次發版)才炸——
/// 所以讓 CI 讀檔確認,而不是靠人記得。
/// </summary>
public class RepoInvariantsTests
{
    private static string Read(params string[] parts) => Repo.Read(parts).Replace("\r\n", "\n");

    [Fact]
    public void Ci_installs_the_browser_before_running_the_tests()
    {
        // 調換順序不會有任何錯誤訊息:SettleCaptureTests 等真瀏覽器測試會自己靜靜略過,
        // 量測正確性就等於沒驗
        var ci = Read(".github", "workflows", "ci.yml");
        var job = Regex.Match(ci, @"^  build-test:\n(?<body>(?:    .*\n|\n)*)", RegexOptions.Multiline);
        Assert.True(job.Success, "ci.yml 找不到 build-test job");

        var install = job.Groups["body"].Value.IndexOf("install-browser", StringComparison.Ordinal);
        var test = job.Groups["body"].Value.IndexOf("run: dotnet test", StringComparison.Ordinal);
        Assert.True(install >= 0 && test >= 0, "build-test 裡要同時有 install-browser 與 dotnet test");
        Assert.True(install < test, "install-browser 必須排在 dotnet test 之前");
    }

    [Fact]
    public void Cli_build_carries_the_playwright_driver_for_every_platform()
    {
        // 0.9.1 真的發出去過:預設只帶建置平台的 node,ubuntu 上 pack 的包 Windows 裝了就 Driver not found。
        // 測試專案引用 Parity.Cli,它的 .playwright/node 跟著複製到這裡——只有 Cli 設了 PlaywrightPlatform=all,
        // 所以這裡看得到五個平台,就證明那一行還在
        Assert.Contains("<PlaywrightPlatform>all</PlaywrightPlatform>", Read("src", "Parity.Cli", "Parity.Cli.csproj"));
        var node = Path.Combine(AppContext.BaseDirectory, ".playwright", "node");
        Assert.Equal(["darwin-arm64", "darwin-x64", "linux-arm64", "linux-x64", "win32_x64"],
            Directory.GetDirectories(node).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Release_triggers_only_on_three_part_version_tags()
    {
        // 'v*' 也吻合移動式的 v1——每次前移都會誤觸發一次注定失敗的 release(run #27/#29)
        var release = Read(".github", "workflows", "release.yml");
        Assert.Matches(new Regex(@"^    tags: \['v\[0-9\]\*\.\[0-9\]\*\.\[0-9\]\*'\]$", RegexOptions.Multiline), release);
        Assert.DoesNotMatch(new Regex(@"tags:.*'v\*'"), release);
    }

    [Fact]
    public void NuGet_login_is_pinned_to_a_commit_sha()
    {
        var release = Read(".github", "workflows", "release.yml");
        var uses = Regex.Matches(release, @"uses: NuGet/login@(?<ref>\S+)").Select(m => m.Groups["ref"].Value).ToList();

        Assert.NotEmpty(uses);
        Assert.All(uses, r => Assert.Matches("^[0-9a-f]{40}$", r));
    }

    [Fact]
    public void Version_fields_stay_placeholders_because_the_tag_is_the_source_of_truth()
    {
        // 寫死真版號一定跟 tag 漂開(從原始碼建出的執行檔會自稱舊版);留空則退回 1.0.0
        Assert.Contains("<Version>0.0.0-dev</Version>", Read("src", "Parity.Cli", "Parity.Cli.csproj"));

        using var pkg = JsonDocument.Parse(Read("npm", "cornhsu-parity", "package.json"));
        Assert.Equal("0.0.0-placeholder", pkg.RootElement.GetProperty("version").GetString());
        Assert.All(pkg.RootElement.GetProperty("optionalDependencies").EnumerateObject(),
            dep => Assert.Equal("0.0.0-placeholder", dep.Value.GetString()));
    }

    [Fact]
    public void Gitignore_keeps_demo_artifacts_and_secrets_out_but_npm_bin_in()
    {
        var lines = Read(".gitignore").Split('\n').Select(l => l.Trim()).ToList();

        // demo 要保持「有未配對、要人工補」的教學情境;本機跑過 map / baseline save 生出來的檔不能進版控
        Assert.Contains("samples/demo/parity.map.json", lines);
        Assert.Contains("samples/demo/parity.baseline.db", lines);
        Assert.Contains(".env", lines);
        Assert.Contains("Parity網頁外殼規畫書.md", lines);
        // npm 主套件的啟動腳本是原始碼——反向規則必須排在 bin/ 之後才生效
        Assert.True(lines.IndexOf("!npm/cornhsu-parity/bin/") > lines.IndexOf("bin/"),
            "`!npm/cornhsu-parity/bin/` 必須在 `bin/` 之後,否則 npm 包會少掉入口點");
    }

    [Fact]
    public void Every_readme_fact_declares_how_many_times_it_must_match()
    {
        // 少了 Occurrences,README 措辭一改、regex 不再命中時,檢查會安靜地變成「零項通過」
        var script = Read("scripts", "verify-readme-facts.ps1");
        var block = Regex.Match(script, @"^\$facts = @\((?<body>.*?)^\)$", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(block.Success, "verify-readme-facts.ps1 找不到 $facts 清單");

        var labels = Regex.Matches(block.Groups["body"].Value, @"@\{ Label = '").Count;
        var occurrences = Regex.Matches(block.Groups["body"].Value, @"Occurrences = \d+").Count;
        Assert.True(labels >= 8, $"只解析到 {labels} 條事實,腳本格式可能變了");
        Assert.Equal(labels, occurrences);
    }

    [Fact]
    public void Dependabot_never_proposes_the_imagesharp_major_upgrade()
    {
        // ImageSharp 4.0 把授權變成建置期強制,沒有授權檔就 CI 全紅(#19 實測)
        Assert.Matches(new Regex(
            @"- dependency-name: ""SixLabors\.ImageSharp""\n\s+update-types: \[""version-update:semver-major""\]"),
            Read(".github", "dependabot.yml"));
    }

    [Theory]
    [InlineData(typeof(ParityServerEntryPoint))]
    [InlineData(typeof(Parity.Server.Data.ServerDbContext))]
    public void The_server_never_references_playwright(Type serverType)
    {
        // 鐵則「雲端不跑瀏覽器」的實體化:編譯器只為真正用到的組件寫入參考,
        // 伺服器端只要碰了任何 Playwright 型別,這裡就看得到
        Assert.DoesNotContain(serverType.Assembly.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("Microsoft.Playwright", StringComparison.Ordinal));
    }
}

/// <summary>
/// npm 通路:同一份平台清單寫在三個地方(組包腳本、主套件的 optionalDependencies、release 的 RID 迴圈),
/// 任一處多一個少一個,使用者那個平台就裝不到或裝了跑不動。
/// </summary>
public class NpmPlatformListTests
{
    private static string Read(params string[] parts) => Repo.Read(parts).Replace("\r\n", "\n");

    /// <summary>prepare.mjs 的 TARGETS:RID → 平台套件名。</summary>
    private static Dictionary<string, string> PrepareTargets()
        => Regex.Matches(Read("npm", "prepare.mjs"), @"\{ rid: ""(?<rid>[^""]+)"", pkg: ""(?<pkg>[^""]+)""")
            .ToDictionary(m => m.Groups["rid"].Value, m => m.Groups["pkg"].Value);

    [Fact]
    public void Prepare_script_package_json_and_release_build_agree_on_platforms()
    {
        var prepare = PrepareTargets();
        Assert.Equal(6, prepare.Count);

        using var pkg = JsonDocument.Parse(Read("npm", "cornhsu-parity", "package.json"));
        var optional = pkg.RootElement.GetProperty("optionalDependencies").EnumerateObject().Select(p => p.Name);
        Assert.Equal(prepare.Values.Select(p => "@cornhsu/" + p).Order(), optional.Order());

        var loop = Regex.Match(Read(".github", "workflows", "release.yml"), @"for rid in (?<rids>[a-z0-9\- ]+); do");
        Assert.True(loop.Success, "release.yml 找不到 publish 的 RID 迴圈");
        Assert.Equal(prepare.Keys.Order(), loop.Groups["rids"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order());
    }

    [Fact]
    public void Launcher_only_points_at_platform_packages_that_are_built()
    {
        // 啟動腳本的 TARGETS 不能指向一個根本不會發佈的套件。另一個方向(發佈了卻沒接上)
        // 目前不成立:啟動腳本缺 win32 arm64 / linux arm64,已另案回報,修好後改成 Assert.Equal
        var launcher = Regex.Matches(Read("npm", "cornhsu-parity", "bin", "parity.js"), @"pkg: ""@cornhsu/(?<pkg>[^""]+)""")
            .Select(m => m.Groups["pkg"].Value).ToList();

        Assert.NotEmpty(launcher);
        Assert.Subset(PrepareTargets().Values.ToHashSet(), launcher.ToHashSet());
    }
}
