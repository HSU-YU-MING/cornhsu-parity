using System.Text.RegularExpressions;

namespace Parity.Tests;

/// <summary>
/// 凍結面 3(GitHub Action inputs)。使用者的 workflow 以 `uses: …/cornhsu-parity@v1` 引用,
/// v1 會被前移——刪掉或改名 input、改預設值,所有人的 CI 會在下一次前移時一起變。
/// 解析刻意只用 regex(不為測試引入 YAML 套件):action.yml 的形狀單純,格式一變這裡就大聲紅。
/// </summary>
public class ActionYmlTests
{
    private static readonly string Yml = Repo.Read("action.yml").Replace("\r\n", "\n");

    /// <summary>頂層 key(column 0)之間的區塊。</summary>
    private static string TopLevelBlock(string key)
    {
        var m = Regex.Match(Yml, $@"^{key}:\n(?<body>(?:(?:[ #].*)?\n)*)", RegexOptions.Multiline);
        Assert.True(m.Success, $"action.yml 沒有頂層 `{key}:` 區塊");
        return m.Groups["body"].Value;
    }

    [Fact]
    public void Inputs_are_the_frozen_eight_with_their_defaults_in_order()
    {
        var inputs = Regex.Matches(TopLevelBlock("inputs"),
                @"^  (?<name>[a-z-]+):\n(?:    (?!required:|default:).*\n)*    required: (?<req>\w+)\n    default: '(?<def>[^']*)'",
                RegexOptions.Multiline)
            .Select(m => (m.Groups["name"].Value, m.Groups["req"].Value, m.Groups["def"].Value))
            .ToList();

        Assert.Equal(
        [
            ("config", "false", "parity.config.json"),
            ("target", "false", ""),
            ("working-directory", "false", "."),
            ("version", "false", ""),
            ("figma-token", "false", ""),
            ("baseline", "false", "false"),
            ("comment", "false", "true"),
            ("upload-report", "false", "true"),
        ], inputs);
        // 每個 input 都被上面的 pattern 吃到了(沒有漏掉一個格式不同的)
        Assert.Equal(8, Regex.Matches(TopLevelBlock("inputs"), @"^  [a-z-]+:\s*$", RegexOptions.Multiline).Count);
    }

    [Fact]
    public void There_are_no_outputs()
    {
        // 固定現況:check 的 exit code 只是內部 step output。新增 outputs = minor
        Assert.DoesNotMatch(new Regex("^outputs:", RegexOptions.Multiline), Yml);
    }

    [Fact]
    public void Run_scripts_never_interpolate_inputs_directly()
    {
        // CLAUDE.md 地雷:`${{ }}` 是 shell 看到指令之前的字串替換,input 接到不可信來源
        // (例如 issue 內文)就變成 runner 上的指令。input 一律先進 env:,再以 shell 變數引用
        var runBlocks = Regex.Matches(Yml,
                @"^(?<indent> +)run: (?:\|\n(?<body>(?:\k<indent> {2}.*\n|\n)*)|(?<line>.+)\n)", RegexOptions.Multiline)
            .Select(m => m.Groups["body"].Success && m.Groups["body"].Length > 0 ? m.Groups["body"].Value : m.Groups["line"].Value)
            .ToList();

        Assert.True(runBlocks.Count >= 5, $"只找到 {runBlocks.Count} 個 run: 區塊,解析可能壞了");
        Assert.All(runBlocks, body => Assert.DoesNotMatch(@"\$\{\{\s*(inputs\.|github\.event)", body));
    }

    [Fact]
    public void Every_input_used_by_a_script_goes_through_env()
    {
        // 上一條的另一半:run 裡要用到的 input 確實有被映射到 env(而不是乾脆被拿掉)
        foreach (var (input, variable) in new[]
        {
            ("version", "PARITY_VERSION"), ("figma-token", "FIGMA_TOKEN"), ("config", "PARITY_CONFIG"),
            ("target", "PARITY_TARGET"), ("baseline", "PARITY_BASELINE"),
        })
            Assert.Matches(new Regex($@"^\s+{variable}: \$\{{\{{ inputs\.{Regex.Escape(input)} \}}\}}$", RegexOptions.Multiline), Yml);
    }

    /// <summary>一個 step(`    - name: …`)到下一個 step 之間的內容。</summary>
    private static string Step(string name)
    {
        var m = Regex.Match(Yml, $@"^    - name: {Regex.Escape(name)}\n(?<body>(?:(?!    - name: ).*\n)*)", RegexOptions.Multiline);
        Assert.True(m.Success, $"action.yml 找不到 step `{name}`");
        return m.Groups["body"].Value;
    }

    // ---------- 真的跑 check 步驟的 bash(regex 比字串殺不掉「拼錯路徑」這種 mutation)----------

    /// <summary>bash:Linux/macOS 用 PATH 上的;Windows 用 Git Bash(GitHub 的 Windows runner 與本機都有)。</summary>
    private static string BashPath()
    {
        if (!OperatingSystem.IsWindows()) return "bash";
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles"), @"C:\Program Files" })
            if (root is not null && File.Exists(Path.Combine(root, "Git", "bin", "bash.exe")))
                return Path.Combine(root, "Git", "bin", "bash.exe");
        throw new InvalidOperationException("找不到 Git Bash(Program Files\\Git\\bin\\bash.exe),這條測試需要它");
    }

    /// <summary>step 的 `run: |` 內容(去掉 8 格縮排)。</summary>
    private static string RunScript(string step)
    {
        var m = Regex.Match(Step(step), @"^      run: \|\n(?<body>(?:(?:        .*)?\n)*)", RegexOptions.Multiline);
        Assert.True(m.Success, $"step `{step}` 沒有 run: | 區塊");
        return string.Join("\n", m.Groups["body"].Value.Split('\n').Select(l => l.Length >= 8 ? l[8..] : l.TrimStart()));
    }

    private sealed record StepRun(int Exit, string Output, string ArtifactDir, bool ParityCalled, string Stderr);

    /// <summary>
    /// 用 runner 的方式(bash -eo pipefail)跑「Run Parity check」那段,parity 換成 stub:
    /// 設定檔存在就照真的 check 把報告寫到「設定檔所在目錄/.parity/report.json」與 --md、exit 1;
    /// 不存在就什麼都不寫、exit 2。stub 被呼叫過會留下 $RUNNER_TEMP/parity-called。
    /// </summary>
    private static StepRun RunCheckStep(string workspace, string workingDirectory, string config, string target = "")
    {
        string Unix(string p) => p.Replace('\\', '/');
        var runnerTemp = Directory.CreateDirectory(Path.Combine(workspace, "_temp")).FullName;
        var output = Path.Combine(runnerTemp, "github_output");
        File.WriteAllText(output, "");
        const string stub = """
            parity() {
              : > "$RUNNER_TEMP/parity-called"
              local cfg="" md=""
              while [ $# -gt 0 ]; do
                case "$1" in
                  --config) cfg="$2"; shift 2 ;;
                  --md) md="$2"; shift 2 ;;
                  *) shift ;;
                esac
              done
              [ -f "$cfg" ] || return 2
              mkdir -p "$(dirname -- "$cfg")/.parity"
              printf 'report for %s' "$cfg" > "$(dirname -- "$cfg")/.parity/report.json"
              printf 'markdown\n' > "$md"
              return 1
            }

            """;
        var script = Path.Combine(runnerTemp, "step.sh");
        File.WriteAllText(script, stub + RunScript("Run Parity check"));

        var psi = new System.Diagnostics.ProcessStartInfo(BashPath())
        {
            WorkingDirectory = Path.Combine(workspace, workingDirectory),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "--noprofile", "--norc", "-eo", "pipefail", Unix(script) }) psi.ArgumentList.Add(a);
        psi.Environment["RUNNER_TEMP"] = Unix(runnerTemp);
        psi.Environment["GITHUB_OUTPUT"] = Unix(output);
        psi.Environment["PARITY_CONFIG"] = config;
        psi.Environment["PARITY_TARGET"] = target;
        psi.Environment["PARITY_BASELINE"] = "false";
        using var p = System.Diagnostics.Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(30_000), "bash 沒有在 30 秒內結束");
        return new StepRun(p.ExitCode, File.ReadAllText(output).Replace("\r\n", "\n"),
            Path.Combine(runnerTemp, "parity-artifact"), File.Exists(Path.Combine(runnerTemp, "parity-called")),
            stderr.GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData(".", "parity.config.json")]
    [InlineData(".", "web/parity.config.json")]        // config 在子目錄:1.1.0 的 artifact 就漏在這裡
    [InlineData("app", "parity.config.json")]          // working-directory 不是 workspace 根
    [InlineData("app", "design/parity.config.json")]
    public void The_artifact_carries_the_report_check_wrote_next_to_the_config(string workingDirectory, string config)
    {
        // check 把 report.json 寫在**設定檔所在目錄**的 .parity/,不是 working-directory。
        // 1.1.0 寫死上傳 <working-directory>/.parity/report.json:config 放子目錄時 artifact 靜靜少一個檔。
        // 報告本身不搬(後續步驟可能照預設路徑讀它),而是複製到 runner.temp 底下固定的目錄再上傳
        using var dir = new TempDir();
        dir.Write(Path.Combine(workingDirectory, config), "{}");

        var r = RunCheckStep(dir.Path, workingDirectory, config);

        Assert.Equal(0, r.Exit); // 這步本身不讓 job 失敗(gate 在最後一步才擋)
        Assert.Equal("exit=1\n", r.Output);
        Assert.Equal($"report for {config}", File.ReadAllText(Path.Combine(r.ArtifactDir, "report.json")));
        Assert.Equal("markdown\n", File.ReadAllText(Path.Combine(r.ArtifactDir, "parity-report.md")).Replace("\r\n", "\n"));
    }

    /// <summary>
    /// 建 symlink。先用系統的 symbolic link;Windows 沒有開發人員模式/管理員權限時建不了,改用 Git Bash 的
    /// 模擬 symlink(MSYS=winsymlinks:sys)——action 的步驟跑在 bash 裡,bash 的 test -L 與 cp 都認得它,
    /// 正好是這條測試要驗的對象。
    /// </summary>
    private static void MakeSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception) when (OperatingSystem.IsWindows()) { }

        var psi = new System.Diagnostics.ProcessStartInfo(BashPath()) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "ln -s -- \"$1\" \"$2\"", "ln", target.Replace('\\', '/'), link.Replace('\\', '/') }) psi.ArgumentList.Add(a);
        psi.Environment["MSYS"] = "winsymlinks:sys";
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"建不了 symlink:{err}");
    }

    /// <summary>建得了 symlink 才跑(見 MakeSymlink);兩種方法都不行就標成略過,看得見。</summary>
    public sealed class SymlinkFactAttribute : FactAttribute
    {
        public SymlinkFactAttribute()
        {
            if (!CanCreateSymlinks())
                Skip = "這台機器建不了 symbolic link(系統的與 Git Bash 模擬的都不行);Linux / macOS 的 CI 會跑這條";
        }

        private static bool CanCreateSymlinks()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"parity-symlink-probe-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "target"), "");
                MakeSymlink(Path.Combine(dir, "link"), Path.Combine(dir, "target"));
                return true;
            }
            catch (Exception) { return false; }
            finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
        }
    }

    [SymlinkFact]
    public void A_report_path_the_repo_turned_into_a_symlink_is_never_copied_into_the_artifact()
    {
        // repo 可以 commit 一個 .parity/report.json → /proc/self/environ 的 symlink:parity 寫入失敗、symlink 留著,
        // cp 跟著 symlink 把這一步自己的環境(含 FIGMA_TOKEN)複製進 artifact;指向 /dev/zero 則寫滿磁碟。
        // 這裡用 workspace 外的「秘密」檔代替;設定檔不存在 → stub 什麼都不寫、exit 2,symlink 原封留著
        using var dir = new TempDir();
        var secret = dir.Write(Path.Combine("outside", "secret.txt"), "FIGMA_TOKEN=figd_secret");
        var ws = Directory.CreateDirectory(dir.File("ws")).FullName;
        Directory.CreateDirectory(Path.Combine(ws, ".parity"));
        MakeSymlink(Path.Combine(ws, ".parity", "report.json"), secret);
        Directory.CreateDirectory(Path.Combine(ws, "_temp"));
        MakeSymlink(Path.Combine(ws, "_temp", "parity-report.md"), secret); // Markdown 那份比照

        var r = RunCheckStep(ws, ".", "parity.config.json");

        Assert.Equal("exit=2\n", r.Output);
        Assert.False(File.Exists(Path.Combine(r.ArtifactDir, "report.json")), "report.json 的 symlink 被跟著複製了");
        Assert.False(File.Exists(Path.Combine(r.ArtifactDir, "parity-report.md")), "Markdown 的 symlink 被跟著複製了");
    }

    [Fact]
    public void A_config_input_with_newlines_cannot_inject_step_outputs()
    {
        // 審查實證的攻擊:input 先進 env 擋得住 shell 注入,擋不住「把值寫進 $GITHUB_OUTPUT」——
        // 值裡的換行會變成新的 output 行。先寫 exit=0,再用 heredoc 把真正的 exit=… 吞掉,
        // Enforce gate 就讀到 0;若上傳路徑也走 step output,還能指定上傳 runner 上的任意檔案
        using var dir = new TempDir();
        const string payload = "x\nexit=0\nreport<<EOF\n/home/runner/work/**/.git/config\nEOF\nskip<<exit=2\ny/parity.config.json";

        var r = RunCheckStep(dir.Path, ".", payload);

        Assert.Equal(0, r.Exit);
        Assert.Equal("exit=2\n", r.Output); // 只有一行 exit code
        Assert.False(File.Exists(Path.Combine(r.ArtifactDir, "report.json")));
    }

    [Fact]
    public void Only_the_exit_code_is_ever_written_to_step_outputs_and_uploads_use_a_fixed_path()
    {
        // 上一條的靜態那一半。所有 runner 會解析的檔案(OUTPUT/ENV/PATH/STATE/STEP_SUMMARY,含 ${VAR} 寫法),
        // 非註解行裡碰到的只准是這兩行:數字 exit code 與工具目錄(值是 runner 的 $HOME,不來自 input)。
        // 第二輪審查的 mutation:別的 step 用 ${GITHUB_OUTPUT} 寫入、把 input 寫進 $GITHUB_PATH,舊的檢查都抓不到
        var touches = Yml.Split('\n')
            .Where(l => !l.TrimStart().StartsWith('#'))
            .Where(l => Regex.IsMatch(l, @"\$\{?GITHUB_(OUTPUT|ENV|PATH|STATE|STEP_SUMMARY)\}?"))
            .Select(l => l.Trim())
            .ToList();
        Assert.Equal(
        [
            "echo \"$HOME/.dotnet/tools\" >> \"$GITHUB_PATH\"",
            "echo \"exit=$?\" >> \"$GITHUB_OUTPUT\"",
        ], touches);

        // 上傳路徑不能來自任何 step output 或 input(那等於讓 input 決定上傳什麼),而且整段 with: 逐行固定:
        // 不准 `path: |` 多行、不准 `/..` 之類的尾巴、不准多掛一行 ${{ inputs.* }}
        Assert.Matches(new Regex(
            @"^      with:\n        name: parity-report\n(?:        #.*\n)*        path: \$\{\{ runner\.temp \}\}/parity-artifact\n        if-no-files-found: ignore\n\n",
            RegexOptions.Multiline), Step("Upload report"));
    }

    [Fact]
    public void Without_a_version_the_action_installs_the_latest_release_of_its_own_major()
    {
        // README 承諾「@v1 跟隨最新 1.x、介面凍結」。不帶範圍的 `dotnet tool install` 裝的是最新穩定版——
        // 2.0.0 一發佈,所有 @v1 的 workflow 就會裝到 2.x。浮動版號 "1.*" 取最高的 1.x 穩定版
        // (NuGet 的 "[1.0.0,2.0.0)" 則會取**最低**的 1.0.0,不能用)
        var install = Step("Install Parity");
        var floating = Regex.Match(install, @"--version ""(?<major>\d+)\.\*""");
        Assert.True(floating.Success, "沒指定 version 時要用 \"<major>.*\" 鎖住 major");
        Assert.DoesNotMatch(new Regex(@"dotnet tool install --global Cornhsu\.Parity\s*$", RegexOptions.Multiline), install);

        // 鎖的 major = CHANGELOG 最新發佈版的 major。發 2.0.0 時這裡會紅,提醒把 action 一起改成 "2.*"
        var latest = Regex.Match(Repo.Read("CHANGELOG.md"), @"^## (?<major>\d+)\.\d+\.\d+", RegexOptions.Multiline);
        Assert.True(latest.Success, "CHANGELOG 找不到已發佈的版本標題");
        Assert.Equal(latest.Groups["major"].Value, floating.Groups["major"].Value);
    }

    [Fact]
    public void Finding_the_previous_comment_looks_through_every_page()
    {
        // `gh api …/issues/$pr/comments` 不分頁只看得到第一頁(預設 30 則,舊的在前):PR 留言超過 30 則、
        // Parity 的留言掉到後面的頁時,每次都找不到標記、每次都 POST 新留言——與「原地更新、不洗版」相反
        var comment = Step("Comment report on PR");
        var lookup = Regex.Match(comment, @"gh api (?<args>[^\n]*(?:\\\n[^\n]*)*)/issues/\$pr/comments""(?<rest>[^\n]*(?:\\\n[^\n]*)*)");
        Assert.True(lookup.Success, "找不到查舊留言的 gh api 呼叫");
        Assert.Contains("--paginate", lookup.Value);
        // --paginate 搭 --jq 是逐頁套用:`.[0]` 會每頁各吐一個,必須逐筆輸出、在 shell 裡取第一筆
        Assert.DoesNotContain(".[0]", lookup.Value);
        Assert.Contains("${ids%%", comment);
    }

    [Fact]
    public void Check_step_records_the_exit_code_and_the_gate_is_enforced_last()
    {
        // 先貼報告、後擋 gate:check 那步不能讓 job 失敗,最後一步才 exit 原始 exit code
        Assert.Contains("set +e", Yml);
        Assert.Contains("echo \"exit=$?\" >> \"$GITHUB_OUTPUT\"", Yml);
        var steps = Regex.Matches(Yml, @"^    - name: (?<n>.+)$", RegexOptions.Multiline).Select(m => m.Groups["n"].Value).ToList();
        Assert.Equal("Enforce gate", steps[^1]);
        Assert.True(steps.IndexOf("Run Parity check") < steps.IndexOf("Comment report on PR"));
        Assert.Contains("PARITY_EXIT: ${{ steps.check.outputs.exit }}", Yml);
    }
}
