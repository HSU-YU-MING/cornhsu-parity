using System.Text.RegularExpressions;

namespace Parity.Tests;

/// <summary>
/// 凍結面 3(GitHub Action inputs)。使用者的 workflow 以 `uses: …/cornhsu-parity@v2` 引用,
/// v2 會被前移——刪掉或改名 input、改預設值,所有人的 CI 會在下一次前移時一起變。
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
    public void Inputs_are_the_frozen_nine_with_their_defaults_in_order()
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
            ("artifact-name", "false", "parity-report"), // 2.0.0 新增(審查 c)
        ], inputs);
        // 每個 input 都被上面的 pattern 吃到了(沒有漏掉一個格式不同的)
        Assert.Equal(9, Regex.Matches(TopLevelBlock("inputs"), @"^  [a-z-]+:\s*$", RegexOptions.Multiline).Count);
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
            ("target", "PARITY_TARGET"), ("baseline", "PARITY_BASELINE"), ("artifact-name", "PARITY_ARTIFACT_NAME"),
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
        return FindGitBash(Environment.GetEnvironmentVariable("PATH"),
                [Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles"), @"C:\Program Files"])
            ?? throw new InvalidOperationException("找不到 Git Bash(PATH 上的 git.exe / bash.exe 或 Program Files\\Git),這條測試需要它");
    }

    /// <summary>
    /// Windows 上找 Git Bash:① PATH 上的 git.exe 反推安裝位置(Git\cmd、Git\bin、Git\mingw64\bin 三種位置);
    /// ② PATH 上的 bash.exe,但跳過 System32 / WindowsApps 底下那個 WSL 啟動器;③ Program Files\Git\bin。
    /// </summary>
    internal static string? FindGitBash(string? pathVariable, IEnumerable<string?> programFilesRoots)
    {
        var dirs = (pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in dirs.Where(d => File.Exists(Path.Combine(d, "git.exe"))))
            foreach (var relative in new[] { new[] { "..", "bin" }, [], new[] { "..", "..", "bin" } })
                if (Path.GetFullPath(Path.Combine([dir, .. relative, "bash.exe"])) is var bash && File.Exists(bash))
                    return bash;

        static bool IsWslLauncher(string dir)
            => dir.TrimEnd('\\', '/').EndsWith("System32", StringComparison.OrdinalIgnoreCase)
            || dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase);
        foreach (var dir in dirs.Where(d => !IsWslLauncher(d)))
            if (File.Exists(Path.Combine(dir, "bash.exe")))
                return Path.GetFullPath(Path.Combine(dir, "bash.exe"));

        foreach (var root in programFilesRoots.OfType<string>())
            if (File.Exists(Path.Combine(root, "Git", "bin", "bash.exe")))
                return Path.Combine(root, "Git", "bin", "bash.exe");
        return null;
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
    private static StepRun RunCheckStep(string workspace, string workingDirectory, string config, string target = "",
        int timeoutMs = 30_000, Action? onTimeout = null, string artifactName = "parity-report", bool emptyRunnerTemp = false)
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
        psi.Environment["RUNNER_TEMP"] = emptyRunnerTemp ? "" : Unix(runnerTemp);
        psi.Environment["GITHUB_OUTPUT"] = Unix(output);
        psi.Environment["PARITY_CONFIG"] = config;
        psi.Environment["PARITY_TARGET"] = target;
        psi.Environment["PARITY_BASELINE"] = "false";
        psi.Environment["PARITY_ARTIFACT_NAME"] = artifactName;
        using var p = System.Diagnostics.Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            // 卡住的步驟不能留在機器上:先讓呼叫端解開卡點(例如替等著讀 FIFO 的 cp 接上寫入端,
            // 讓它自己結束——Windows 上 Git Bash 的子行程不一定會被 Kill(entireProcessTree) 一起帶走,
            // 留下來的 cp 會握著 testhost 的輸出管線,整個 dotnet test 跟著卡住),再殺整棵行程樹
            onTimeout?.Invoke();
            if (!p.WaitForExit(5_000)) p.Kill(entireProcessTree: true);
            Assert.Fail($"bash 沒有在 {timeoutMs / 1000} 秒內結束(卡住了)");
        }
        return new StepRun(p.ExitCode, File.ReadAllText(output).Replace("\r\n", "\n"),
            Path.Combine(runnerTemp, "parity-artifact"), File.Exists(Path.Combine(runnerTemp, "parity-called")),
            stderr.GetAwaiter().GetResult());
    }

    /// <summary>跑「Install Parity」那段,dotnet 換成 stub(把收到的參數一行一筆記下來)。</summary>
    private static (int Exit, string DotnetCalls, string Stderr) RunInstallStep(string workspace, string version)
    {
        string Unix(string p) => p.Replace('\\', '/');
        var runnerTemp = Directory.CreateDirectory(Path.Combine(workspace, "_temp")).FullName;
        var calls = Path.Combine(runnerTemp, "dotnet-calls");
        const string stub = """
            dotnet() { printf '%s|' "$@" >> "$RUNNER_TEMP/dotnet-calls"; printf '\n' >> "$RUNNER_TEMP/dotnet-calls"; }

            """;
        var script = Path.Combine(runnerTemp, "install.sh");
        File.WriteAllText(script, stub + RunScript("Install Parity"));
        var psi = new System.Diagnostics.ProcessStartInfo(BashPath())
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "--noprofile", "--norc", "-eo", "pipefail", Unix(script) }) psi.ArgumentList.Add(a);
        psi.Environment["RUNNER_TEMP"] = Unix(runnerTemp);
        psi.Environment["GITHUB_PATH"] = Unix(Path.Combine(runnerTemp, "github_path"));
        psi.Environment["HOME"] = Unix(workspace);
        psi.Environment["PARITY_VERSION"] = version;
        using var p = System.Diagnostics.Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(30_000), "bash 沒有在 30 秒內結束");
        return (p.ExitCode, File.Exists(calls) ? File.ReadAllText(calls).Replace("\r\n", "\n") : "", stderr.GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData("", "tool|install|--global|Cornhsu.Parity|--version|2.*|\n")]
    [InlineData("1.1.0", "tool|install|--global|Cornhsu.Parity|--version|1.1.0|\n")]
    public void Install_passes_the_version_or_the_floating_major_to_dotnet(string version, string expected)
    {
        using var dir = new TempDir();

        var r = RunInstallStep(dir.Path, version);

        Assert.Equal(0, r.Exit);
        Assert.Equal(expected, r.DotnetCalls);
    }

    [Theory]
    [InlineData("2.*\n::error title=Parity::INJECTED")]
    [InlineData("1.1.0\r")]
    public void A_version_with_a_line_break_is_refused_before_dotnet_prints_it(string version)
    {
        // 驗證者實證:dotnet tool install --version $'1.*\n::error …' 的錯誤訊息會把版本原樣印出,
        // 第二行落在行首就成了 runner 解析的 workflow command。這一步本來遇到換行就會失敗,
        // 所以提早拒絕不改變任何行為,只是不讓 input 被印出來
        using var dir = new TempDir();

        var r = RunInstallStep(dir.Path, version);

        Assert.NotEqual(0, r.Exit);
        Assert.Equal("", r.DotnetCalls); // dotnet 沒被呼叫
        Assert.Contains("line break", r.Stderr);
        Assert.DoesNotContain("::", r.Stderr);
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
    private static void MakeSymlink(string link, string target, bool directory = false)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(link, target);
            else File.CreateSymbolicLink(link, target);
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

    [SymlinkFact]
    public void A_parity_directory_the_repo_turned_into_a_symlink_is_never_copied_from()
    {
        // 檔案本身是一般檔案,但 .parity 目錄是指向 workspace 外的 symlink:test -L 只看檔案擋不住,
        // 要連所在目錄一起看(驗證者的 mutation M1c 拿掉目錄層的檢查,原本的測試照綠)
        using var dir = new TempDir();
        var outside = Directory.CreateDirectory(dir.File("outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "report.json"), "FIGMA_TOKEN=figd_secret");
        var ws = Directory.CreateDirectory(dir.File("ws")).FullName;
        MakeSymlink(Path.Combine(ws, ".parity"), outside, directory: true);

        var r = RunCheckStep(ws, ".", "parity.config.json"); // 設定檔不存在 → stub 不寫檔、exit 2

        Assert.Equal("exit=2\n", r.Output);
        Assert.False(File.Exists(Path.Combine(r.ArtifactDir, "report.json")), "symlink 目錄裡的 report.json 被複製了");
    }

    /// <summary>用 bash 的 mkfifo 建具名管線(Linux/macOS 原生;Windows 的 Git Bash 也有 MSYS 版的)。</summary>
    private static bool TryMakeFifo(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(BashPath()) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in new[] { "-c", "mkfifo -- \"$1\"", "mkfifo", path.Replace('\\', '/') }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardError.ReadToEnd();
            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>替卡在 FIFO 讀取端的行程接上寫入端(寫一個位元組就關),讓它自己結束;5 秒內接不上就放棄。</summary>
    private static void FeedFifo(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(BashPath()) { UseShellExecute = false };
        foreach (var a in new[] { "-c", "printf x > \"$1\"", "feed", path.Replace('\\', '/') }) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        if (!p.WaitForExit(5_000)) p.Kill(entireProcessTree: true);
    }

    /// <summary>建得了 FIFO 才跑;建不了就標成略過,看得見。</summary>
    public sealed class FifoFactAttribute : FactAttribute
    {
        public FifoFactAttribute()
        {
            var probe = Path.Combine(Path.GetTempPath(), $"parity-fifo-probe-{Guid.NewGuid():N}");
            try
            {
                if (!TryMakeFifo(probe)) Skip = "這台機器的 bash 建不了 FIFO(mkfifo);Linux / macOS 的 CI 會跑這條";
            }
            finally { try { File.Delete(probe); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    [FifoFact]
    public void A_fifo_the_repo_left_at_the_report_path_does_not_hang_the_step()
    {
        // 只收一般檔案(-f)也擋掉 FIFO 與裝置檔:cp 從沒有寫入端的 FIFO 讀,會卡到 job 逾時。
        // 驗證者的 mutation M1d 把 -f 改成 -e,原本沒有測試抓得到
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.File(".parity"));
        Assert.True(TryMakeFifo(dir.File(".parity", "report.json")), "建不了 FIFO");

        var fifo = dir.File(".parity", "report.json");
        var r = RunCheckStep(dir.Path, ".", "parity.config.json", timeoutMs: 15_000, // 設定檔不存在 → stub exit 2
            onTimeout: () => FeedFifo(fifo));

        Assert.Equal("exit=2\n", r.Output);
        Assert.False(File.Exists(Path.Combine(r.ArtifactDir, "report.json")));
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

    [Theory]
    [InlineData("x\n::add-mask::secret\ny/parity.config.json", "")]
    [InlineData("parity.config.json\r", "")]
    [InlineData("parity.config.json", "/\n::set-output name=exit::0")]
    [InlineData("parity.config.json", "/\r")]
    public void A_config_or_target_with_a_line_break_is_refused_before_parity_runs(string config, string target)
    {
        // parity 會把 config/target 原樣印到 stderr;帶換行時,換行後的內容會出現在行首,
        // 變成 runner 解析的 workflow command(::add-mask::、::set-output…)。目前無害,但安全性
        // 不該靠 runner 的處理順序——在呼叫 parity 之前就拒絕,當成執行錯誤(exit 2)
        using var dir = new TempDir();
        dir.Write("parity.config.json", "{}");

        var r = RunCheckStep(dir.Path, ".", config, target);

        Assert.Equal(0, r.Exit); // 這一步照樣不讓 job 失敗,gate 在最後一步擋
        Assert.Equal("exit=2\n", r.Output);
        Assert.False(r.ParityCalled, "含換行的 input 不該交給 parity");
        Assert.Contains("line break", r.Stderr);
        Assert.DoesNotContain("::", r.Stderr); // 錯誤訊息本身不能把 input 印出來
    }

    [Fact]
    public void A_plain_config_still_reaches_parity()
    {
        using var dir = new TempDir();
        dir.Write("parity.config.json", "{}");

        var r = RunCheckStep(dir.Path, ".", "parity.config.json", "/");

        Assert.True(r.ParityCalled);
        Assert.Equal("exit=1\n", r.Output);
    }

    [Fact]
    public void Only_the_exit_code_is_ever_written_to_step_outputs_and_uploads_use_a_fixed_path()
    {
        // 上一條的靜態那一半。所有 runner 會解析的檔案(OUTPUT/ENV/PATH/STATE/STEP_SUMMARY,含 ${VAR} 寫法),
        // 非註解行裡碰到的只准是這三種:常數 exit=2(含換行的 config/target、不合法的 artifact-name 被拒時,各一行)、
        // 數字 exit code、工具目錄(值是 runner 的 $HOME,不來自 input)。
        // 第二輪審查的 mutation:別的 step 用 ${GITHUB_OUTPUT} 寫入、把 input 寫進 $GITHUB_PATH,舊的檢查都抓不到
        var touches = Yml.Split('\n')
            .Where(l => !l.TrimStart().StartsWith('#'))
            .Where(l => Regex.IsMatch(l, @"\$\{?GITHUB_(OUTPUT|ENV|PATH|STATE|STEP_SUMMARY)\}?"))
            .Select(l => l.Trim())
            .ToList();
        string[] allowed =
        [
            "echo \"$HOME/.dotnet/tools\" >> \"$GITHUB_PATH\"",
            "echo \"exit=2\" >> \"$GITHUB_OUTPUT\"",
            "echo \"exit=2\" >> \"$GITHUB_OUTPUT\"",
            "echo \"exit=$?\" >> \"$GITHUB_OUTPUT\"",
        ];
        // 紅了的下一步:確認新的那一行寫進去的值不來自任何 input 或 step output 之後,才把它加進 allowed
        Assert.True(allowed.SequenceEqual(touches),
            "action.yml 裡碰到 $GITHUB_OUTPUT/ENV/PATH/STATE/STEP_SUMMARY 的行與允許清單不同。實際:\n  "
            + string.Join("\n  ", touches)
            + "\n下一步:確認新的那一行寫進去的值不來自任何 input 或 step output(值帶換行就能多寫一筆)之後,"
            + "再把它加進這條測試的 allowed;值可能來自 input 的話,改成固定值,或先擋 \\n/\\r。");

        // 上傳路徑不能來自任何 step output 或 input(那等於讓 input 決定上傳什麼),而且整段 with: 逐行固定:
        // 不准 `path: |` 多行、不准 `/..` 之類的尾巴、不准多掛一行 ${{ inputs.* }}。
        // 唯一來自 input 的是 name(artifact-name,2.0.0):with: 不經過 shell,upload-artifact 自己也驗名稱;
        // 它刻意不進 path——名稱 `..` 會讓 path 變成整個 runner.temp
        Assert.Matches(new Regex(
            @"^      with:\n        name: \$\{\{ inputs\.artifact-name \}\}\n(?:        #.*\n)*        path: \$\{\{ runner\.temp \}\}/parity-artifact\n        if-no-files-found: ignore\n\n",
            RegexOptions.Multiline), Step("Upload report"));
    }

    [Fact]
    public void Without_a_version_the_action_installs_the_latest_release_of_its_own_major()
    {
        // README 承諾「@v2 跟隨最新 2.x、介面凍結」。不帶範圍的 `dotnet tool install` 裝的是最新穩定版——
        // 下一個 major 一發佈,所有 @v2 的 workflow 就會跨過去(v1.1.0 正是這樣,@v1 會裝到 2.x)。
        // 浮動版號 "2.*" 取最高的 2.x 穩定版(NuGet 的 "[2.0.0,3.0.0)" 則會取**最低**的 2.0.0,不能用)
        var install = Step("Install Parity");
        var floating = Regex.Match(install, @"--version ""(?<major>\d+)\.\*""");
        Assert.True(floating.Success, "沒指定 version 時要用 \"<major>.*\" 鎖住 major");
        Assert.DoesNotMatch(new Regex(@"dotnet tool install --global Cornhsu\.Parity\s*$", RegexOptions.Multiline), install);

        // 鎖的 major = CHANGELOG 最新發佈版的 major。發 3.0.0 時這裡會紅,提醒把 action 一起改成 "3.*"
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
    public void Only_the_actions_bot_own_comment_is_ever_updated()
    {
        // 原本取「最舊的、以 marker 開頭」的留言,不看作者:任何人搶先貼一則以 marker 開頭的留言,
        // bot 就會去 PATCH 那則(GITHUB_TOKEN 改不了別人的留言 → 403,報告永遠貼不上),或改到別人的內容。
        // 這一步用的是 github.token,留言作者一定是 github-actions[bot]
        var comment = Step("Comment report on PR");
        Assert.Contains("GH_TOKEN: ${{ github.token }}", comment);
        var jq = Regex.Match(comment, @"--jq (?<q>'.*')\)$", RegexOptions.Multiline);
        Assert.True(jq.Success, "找不到查舊留言的 --jq");
        // 整段完全比對,不比子字串:驗證者的 mutation 把 and 改成 or(M3b)、讓作者條件變成死分支(M3d),
        // 子字串都還在、測試照綠——實跑卻會去 PATCH 別人的留言。改這段 jq 時,先用 jq/gojq 對樣本實跑
        // (別人的 marker 留言要被排除、別的 artifact-name 的留言要被排除、取到 bot 最舊的那則),再更新這裡。
        // marker 由 shell 拼進單引號字串(名稱已驗過只含 [A-Za-z0-9._-]);展開後的樣子見下一條測試
        Assert.Equal(
            "'.[] | select(.user.login == \"github-actions[bot]\" and (.body | startswith(\"'\"$marker\"'\"))) | .id'",
            jq.Groups["q"].Value);
    }

    private sealed record CommentRun(int Exit, string GhCalls, string Body, string Stderr);

    /// <summary>
    /// 跑「Comment report on PR」那段,gh 換成 stub:每次呼叫把參數一個一行記下來(呼叫之間以 ---- 分隔);
    /// 查舊留言(--paginate)時印出 <paramref name="existingIds"/>,當成 jq 篩完的結果。
    /// </summary>
    private static CommentRun RunCommentStep(string workspace, string artifactName, string existingIds, string report = "markdown\n")
    {
        string Unix(string p) => p.Replace('\\', '/');
        var runnerTemp = Directory.CreateDirectory(Path.Combine(workspace, "_temp")).FullName;
        File.WriteAllText(Path.Combine(runnerTemp, "parity-report.md"), report);
        foreach (var stale in new[] { "gh-calls", "parity-comment.md" }) File.Delete(Path.Combine(runnerTemp, stale));
        const string stub = """
            gh() {
              printf '%s\n' "$@" >> "$RUNNER_TEMP/gh-calls"
              printf -- '----\n' >> "$RUNNER_TEMP/gh-calls"
              case " $* " in *" --paginate "*) printf '%s' "$GH_STUB_IDS" ;; esac
            }

            """;
        var script = Path.Combine(runnerTemp, "comment.sh");
        File.WriteAllText(script, stub + RunScript("Comment report on PR"));
        var psi = new System.Diagnostics.ProcessStartInfo(BashPath())
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "--noprofile", "--norc", "-eo", "pipefail", Unix(script) }) psi.ArgumentList.Add(a);
        psi.Environment["RUNNER_TEMP"] = Unix(runnerTemp);
        psi.Environment["GITHUB_REPOSITORY"] = "owner/repo";
        psi.Environment["PR_NUMBER"] = "7";
        psi.Environment["GH_TOKEN"] = "unused";
        psi.Environment["PARITY_ARTIFACT_NAME"] = artifactName;
        psi.Environment["GH_STUB_IDS"] = existingIds;
        using var p = System.Diagnostics.Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(30_000), "bash 沒有在 30 秒內結束");
        string ReadOrEmpty(string name) => File.Exists(Path.Combine(runnerTemp, name))
            ? File.ReadAllText(Path.Combine(runnerTemp, name)).Replace("\r\n", "\n") : "";
        return new CommentRun(p.ExitCode, ReadOrEmpty("gh-calls"), ReadOrEmpty("parity-comment.md"), stderr.GetAwaiter().GetResult());
    }

    [Theory]
    [InlineData("parity-report", "<!-- parity-report -->")]     // 預設名稱沿用 1.x 的 marker:升級後照樣更新原本那則
    [InlineData("site-a", "<!-- parity-report:site-a -->")]
    [InlineData("web_v2.1", "<!-- parity-report:web_v2.1 -->")]
    [InlineData(".hidden-ok", "<!-- parity-report:.hidden-ok -->")] // 點開頭但不全是點、- 不在開頭:照收
    [InlineData("site-", "<!-- parity-report:site- -->")]
    public void Each_artifact_name_keeps_its_own_pr_comment(string artifactName, string marker)
    {
        // 審查 c:matrix 或同一個 job 呼叫兩次時,原本共用同一則留言,後一次把前一次的報告蓋掉。
        // marker 帶上 artifact-name,查舊留言與新留言都用它
        using var dir = new TempDir();

        var found = RunCommentStep(dir.Path, artifactName, "111\n222");
        var none = RunCommentStep(dir.Path, artifactName, "");

        Assert.Equal(0, found.Exit);
        Assert.StartsWith(marker + "\n", found.Body);
        var calls = found.GhCalls.Split("----\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, calls.Length);
        Assert.Contains(
            $".[] | select(.user.login == \"github-actions[bot]\" and (.body | startswith(\"{marker}\"))) | .id\n", calls[0]);
        Assert.StartsWith("api\n-X\nPATCH\nrepos/owner/repo/issues/comments/111\n", calls[1]); // 最舊的那則
        Assert.Contains("\nPOST\nrepos/owner/repo/issues/7/comments\n", none.GhCalls);
    }

    [Fact]
    public void A_truncated_comment_points_at_the_artifact_by_its_name()
    {
        using var dir = new TempDir();

        var r = RunCommentStep(dir.Path, "site-a", "", report: new string('x', 60_001));

        Assert.Contains("see the site-a artifact", r.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("x\\")]
    [InlineData("x\"y")]
    [InlineData("x\n::error title=Parity::INJECTED")]
    [InlineData("parity-report\r")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 101 字
    // 審查第 7 條:字元集內、但當成名稱或參數有歧義的——全是點(`.`、`..` 像路徑)、以 - 開頭(像旗標)
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("-rf")]
    [InlineData("-")]
    public void An_artifact_name_outside_the_safe_charset_is_refused(string artifactName)
    {
        // artifact-name 會拼進 PR 留言的 marker,而 marker 又拼進 jq 字串:只收 [A-Za-z0-9._-]{1,100}。
        // check 步驟拒跑(exit 2,照常由最後一步擋);留言步驟自己再驗一次、不貼,不靠步驟順序
        using var dir = new TempDir();
        dir.Write("parity.config.json", "{}");

        var check = RunCheckStep(dir.Path, ".", "parity.config.json", artifactName: artifactName);
        var comment = RunCommentStep(dir.Path, artifactName, "111");

        Assert.Equal(0, check.Exit);
        Assert.Equal("exit=2\n", check.Output);
        Assert.False(check.ParityCalled);
        Assert.Contains("artifact-name", check.Stderr);
        Assert.DoesNotContain("::", check.Stderr); // 不把 input 印出來
        Assert.Equal(0, comment.Exit);
        Assert.Equal("", comment.GhCalls);
        Assert.DoesNotContain("::", comment.Stderr);
    }

    [Fact]
    public void A_call_that_fails_before_writing_a_report_never_ships_the_previous_calls_report()
    {
        // 同一個 job 呼叫兩次:$RUNNER_TEMP 共用。第二次若在寫出報告前就失敗(設定檔不存在、名稱不合法…),
        // 原本會把第一次留下的 report.json / Markdown 當成自己的上傳、貼到自己的留言
        using var dir = new TempDir();
        dir.Write("_temp/parity-artifact/report.json", "first call");
        dir.Write("_temp/parity-artifact/parity-report.md", "first call");
        dir.Write("_temp/parity-report.md", "first call");

        var r = RunCheckStep(dir.Path, ".", "missing.config.json", artifactName: "second");

        Assert.Equal("exit=2\n", r.Output);
        Assert.Empty(Directory.Exists(r.ArtifactDir) ? Directory.GetFiles(r.ArtifactDir) : []);
        Assert.False(File.Exists(Path.Combine(dir.Path, "_temp", "parity-report.md")), "上一次的 Markdown 還在,留言步驟會把它貼出去");
    }

    [Fact]
    public void Every_rm_in_the_action_is_guarded_against_an_empty_runner_temp()
    {
        // 審查第 9 條:`rm -rf -- "$RUNNER_TEMP/parity-artifact"` 在 RUNNER_TEMP 沒設時會變成 rm -rf /parity-artifact。
        // runner 一定會設,但刪檔的那一行不該靠這個假設:一律寫成 ${RUNNER_TEMP:?},沒設就讓這一步失敗
        var rmLines = Yml.Split('\n').Where(l => !l.TrimStart().StartsWith('#') && Regex.IsMatch(l, @"\brm\b")).ToList();
        Assert.NotEmpty(rmLines);
        Assert.All(rmLines, l =>
        {
            Assert.DoesNotMatch(@"\$RUNNER_TEMP|\$\{RUNNER_TEMP\}", l);
            Assert.Contains("${RUNNER_TEMP:?}", l);
        });
    }

    [Fact]
    public void An_empty_runner_temp_stops_the_check_step_before_it_deletes_or_runs_anything()
    {
        using var dir = new TempDir();
        dir.Write("parity.config.json", "{}");

        var r = RunCheckStep(dir.Path, ".", "parity.config.json", emptyRunnerTemp: true);

        Assert.NotEqual(0, r.Exit);
        Assert.Contains("RUNNER_TEMP", r.Stderr);
        Assert.False(r.ParityCalled);
        Assert.Equal("", r.Output);
    }

    [Fact]
    public void The_upload_is_skipped_when_the_check_step_never_ran()
    {
        // 前面的步驟(Setup .NET、Install)失敗時 check 不會跑,但 Upload 是 always():不擋的話會把同一個 job
        // 前一次呼叫留在 $RUNNER_TEMP 的報告,用這一次的名稱上傳。check 只要跑了就一定寫 exit=…
        Assert.Matches(new Regex(
                @"^      if: \$\{\{ always\(\) && inputs\.upload-report == 'true' && steps\.check\.outputs\.exit != '' \}\}$",
                RegexOptions.Multiline),
            Step("Upload report"));
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
