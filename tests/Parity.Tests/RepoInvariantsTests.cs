using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/release.yml")]
    [InlineData("action.yml")]
    public void Every_action_is_pinned_to_a_commit_sha(string file)
    {
        // 移動式 tag(@v7)可以被改指到任何 commit,包括上游被入侵之後的那一個;釘 40 碼 SHA 才是審過的那一份。
        // action.yml 尤其要釘:它跑在使用者的 runner 上。版本寫在行尾註解(# v7.0.1),
        // dependabot 的 github-actions 更新會連同註解一起改
        var uses = Regex.Matches(Read(file.Split('/')), @"^\s*(?:-\s+)?uses:\s*(?<ref>\S+)", RegexOptions.Multiline)
            .Select(m => m.Groups["ref"].Value)
            .ToList();

        Assert.NotEmpty(uses);
        var unpinned = uses.Where(u => !u.StartsWith("./", StringComparison.Ordinal) && !Regex.IsMatch(u, "@[0-9a-f]{40}$")).ToList();
        Assert.True(unpinned.Count == 0, $"{file} 有沒釘 commit SHA 的 uses:{string.Join(", ", unpinned)}");
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

    /// <summary>
    /// 引擎的「擷取端」:開瀏覽器的型別都在這個命名空間,加上把擷取接進比對的兩個入口。
    /// 伺服器正當地用到引擎的報告型別(ReportWire / FidelityScore / BaselineComparer…),
    /// 所以不能整個 Parity.Engine 都禁,只禁這一側。
    /// </summary>
    private const string EngineCaptureNamespace = "Parity.Engine.ImplementationSources.Web";

    private static readonly string[] EngineCaptureEntryPoints =
    [
        "Parity.Engine.FidelityEngine",
        "Parity.Engine.ImplementationSources.IImplementationSource",
    ];

    /// <summary>組件中繼資料裡的 TypeReference(編譯器只為程式真正用到的外部型別寫入)。</summary>
    private static List<(string Namespace, string FullName)> TypeReferencesOf(Assembly assembly)
    {
        using var pe = new PEReader(File.OpenRead(assembly.Location));
        var md = pe.GetMetadataReader();
        return md.TypeReferences
            .Select(h => md.GetTypeReference(h))
            .Select(t => (Ns: md.GetString(t.Namespace), Name: md.GetString(t.Name)))
            .Select(t => (t.Ns, t.Ns.Length == 0 ? t.Name : $"{t.Ns}.{t.Name}"))
            .ToList();
    }

    [Theory]
    [InlineData(typeof(ParityServerEntryPoint))]
    [InlineData(typeof(Parity.Server.Data.ServerDbContext))]
    public void The_server_never_references_playwright_or_the_engine_capture_side(Type serverType)
    {
        // 鐵則「雲端不跑瀏覽器」的實體化:編譯器只為真正用到的組件寫入參考,
        // 伺服器端只要碰了任何 Playwright 型別,這裡就看得到
        Assert.DoesNotContain(serverType.Assembly.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("Microsoft.Playwright", StringComparison.Ordinal));

        // 只看 Playwright 組件參考不夠:引擎的 WebImplementationSource 公開 API 沒有 Playwright 型別,
        // 伺服器經由共用的 Parity.Engine 呼叫它就能開 Chromium,上面那條照樣綠。
        // 所以再掃 TypeReference:擷取端的型別一個都不准出現
        var offending = TypeReferencesOf(serverType.Assembly)
            .Where(t => t.Namespace.StartsWith("Microsoft.Playwright", StringComparison.Ordinal)
                || t.Namespace == EngineCaptureNamespace
                || EngineCaptureEntryPoints.Contains(t.FullName))
            .Select(t => t.FullName)
            .ToList();
        Assert.Empty(offending);
    }

    [Fact]
    public void Nothing_the_server_builds_from_can_reach_playwright()
    {
        // 鐵則的**結構性**保證:Parity.Server.csproj 自己不引用 Playwright 還不夠——到 2026-10-22 為止,
        // Server.Data → Parity.Engine 把 Microsoft.Playwright 與整個 node driver(~103 MB)帶進伺服器產物,
        // 上面那條只掃型別參考,反射載入照樣擋不住。這裡沿 ProjectReference 走完整張圖:伺服器建置用到的
        // 每一個專案都不准有 Playwright 套件,也不准是引擎本身(報告型別在不含擷取端的 Parity.Report)
        var visited = new List<string>();
        void Walk(string csproj)
        {
            var full = Path.GetFullPath(csproj);
            if (visited.Contains(full)) return;
            visited.Add(full);
            foreach (Match m in Regex.Matches(File.ReadAllText(full), @"<ProjectReference Include=""(?<path>[^""]+)"""))
                Walk(Path.Combine(Path.GetDirectoryName(full)!, m.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar)));
        }
        Walk(Repo.PathOf("src", "Parity.Server", "Parity.Server.csproj"));

        Assert.True(visited.Count >= 3, $"只走到 {visited.Count} 個專案,ProjectReference 解析可能壞了");
        Assert.DoesNotContain(visited, p => Path.GetFileName(p) == "Parity.Engine.csproj");
        Assert.All(visited, p => Assert.DoesNotMatch(@"<PackageReference Include=""Microsoft\.Playwright", File.ReadAllText(p)));

        // 編譯後也一樣:伺服器兩個組件都不引用引擎組件
        foreach (var asm in new[] { typeof(ParityServerEntryPoint).Assembly, typeof(Parity.Server.Data.ServerDbContext).Assembly })
            Assert.DoesNotContain(asm.GetReferencedAssemblies(), a => a.Name == "Parity.Engine");
    }

    [Fact]
    public void Every_engine_type_that_drives_playwright_is_on_the_server_ban_list()
    {
        // 上一條的禁用清單是手寫的:引擎哪天在別的命名空間多一個開瀏覽器的型別,禁用清單就漏了。
        // 這裡從引擎組件反推「欄位/方法簽章(含編譯器產生的 async 狀態機與閉包)用到 Playwright 型別」
        // 的最外層型別,它們必須全部落在禁用的命名空間裡
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        static bool IsPlaywright(Type t)
            => !t.IsGenericParameter
            && (t.Assembly.GetName().Name!.StartsWith("Microsoft.Playwright", StringComparison.Ordinal)
                || (t.HasElementType && IsPlaywright(t.GetElementType()!))
                || (t.IsGenericType && t.GetGenericArguments().Any(IsPlaywright)));

        static bool Touches(Type t)
            => t.GetFields(all).Any(f => IsPlaywright(f.FieldType))
            || t.GetMethods(all).Any(m => IsPlaywright(m.ReturnType) || m.GetParameters().Any(p => IsPlaywright(p.ParameterType)))
            || t.GetConstructors(all).Any(c => c.GetParameters().Any(p => IsPlaywright(p.ParameterType)))
            || t.GetInterfaces().Any(IsPlaywright)
            || (t.BaseType is { } b && IsPlaywright(b));

        static Type Outermost(Type t) => t.DeclaringType is { } d ? Outermost(d) : t;

        var drivers = typeof(Parity.Engine.FidelityEngine).Assembly.GetTypes()
            .Where(Touches).Select(Outermost).Distinct().ToList();

        // 偵測本身要有效:WebImplementationSource 的 IBrowser 欄位一定抓得到,抓不到就是這條測試壞了
        Assert.Contains(typeof(Parity.Engine.ImplementationSources.Web.WebImplementationSource), drivers);
        Assert.All(drivers, t => Assert.Equal(EngineCaptureNamespace, t.Namespace));
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
    public void Npm_engines_node_matches_what_the_bundled_playwright_driver_requires()
    {
        // npm 通路把 PLAYWRIGHT_NODEJS_PATH 指向使用者自己的 Node(啟動腳本),所以 driver 的 Node 下限
        // 就是 npm 包的 Node 下限。Playwright 1.63 把它從 18 提到 20,主包卻還宣告 >=18:
        // npm 安裝時不警告,Node 18 使用者連 install-browser 都會失敗。升 Playwright 時這裡會紅
        using var driver = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "package.json")));
        var required = driver.RootElement.GetProperty("engines").GetProperty("node").GetString();

        using var pkg = JsonDocument.Parse(Read("npm", "cornhsu-parity", "package.json"));
        Assert.Equal(required, pkg.RootElement.GetProperty("engines").GetProperty("node").GetString());
    }

    [Fact]
    public void Launcher_points_at_exactly_the_platform_packages_that_are_built()
    {
        // 兩個方向都要成立:指向不會發佈的套件 → 裝不到;發佈了卻沒接上 → 裝得到卻跑不動
        // (1.1.0 的啟動腳本就缺 win32 arm64 / linux arm64,npx 印「沒有預先建置版本」exit 1)
        var launcher = Regex.Matches(Read("npm", "cornhsu-parity", "bin", "parity.js"),
                @"""(?<os>[a-z0-9]+) (?<cpu>[a-z0-9]+)"": \{ pkg: ""@cornhsu/(?<pkg>[^""]+)""")
            .Select(m => (Key: $"{m.Groups["os"].Value} {m.Groups["cpu"].Value}", Pkg: m.Groups["pkg"].Value))
            .ToList();

        Assert.Equal(PrepareTargets().Values.Order(), launcher.Select(l => l.Pkg).Order());
        // 平台鍵(process.platform + process.arch)與套件名的 os/cpu 一致
        Assert.All(launcher, l => Assert.Equal("parity-" + l.Key.Replace(' ', '-'), l.Pkg));
    }
}
