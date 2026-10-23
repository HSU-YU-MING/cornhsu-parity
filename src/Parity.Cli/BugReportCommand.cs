using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Parity.Engine;

namespace Parity.Cli;

/// <summary>
/// <c>parity bug-report</c>(2.0.0,實驗性,不列入凍結)。在本機整理診斷資訊,印出來給使用者看,
/// 再給一個預填好的 GitHub issue 網址(錯誤回報範本)。
/// **鐵則:不自動送出任何資料**——不連網;網址只印出來,<c>--open</c> 時才交給瀏覽器開(開了也只是表單,
/// 使用者自己看過、自己按送出)。可收:版本、作業系統與架構、.NET、Node、Playwright 與 Chromium、安裝通路,
/// 以及設定檔的結構摘要(<see cref="ConfigSummary"/>)。整份文字最後再過一次 <see cref="Redaction"/>。
/// </summary>
internal static partial class BugReportCommand
{
    /// <summary>預填網址的長度上限:GitHub 與瀏覽器對很長的網址不可靠,超過就只帶摘要欄位。</summary>
    internal const int MaxUrlLength = 6000;

    /// <summary>--open 用;測試換掉它,不真的開瀏覽器。</summary>
    internal static Func<string, bool> OpenInBrowser = url =>
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    };

    public static int Run(string[] args)
    {
        var opts = CliOptions.Parse(args, Usage.Flags.BugReport);
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.BugReport);

        var environment = CollectEnvironment();
        var sensitive = new List<string>(Redaction.SecretEnvironmentValues());
        var body = new StringBuilder();
        void Line(string s = "") => body.Append(s).Append('\n');
        Line("### Environment");
        foreach (var (label, value, _) in environment) Line($"- {label}: {value}");
        Line();
        Line("### Config (structure only)");
        if (opts.GetValueOrDefault("--config") is { } configPath)
        {
            var full = Path.GetFullPath(configPath);
            sensitive.Add(full);
            sensitive.Add(Path.GetDirectoryName(full) ?? full);
            try
            {
                var config = ParityConfig.Load(full);
                sensitive.AddRange(SensitiveValuesOf(config));
                foreach (var line in ConfigSummary.From(config).Lines()) Line($"- {line}");
            }
            catch (Exception ex)
            {
                // 例外訊息常帶路徑與設定值:只報型別
                Line($"- config: could not be loaded ({ex.GetType().Name})");
            }
        }
        else
        {
            Line("- config: not given (add --config <path> for a structural summary)");
        }
        sensitive.Add(Environment.CurrentDirectory);
        sensitive.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var text = Redaction.Apply(body.ToString(), sensitive);

        var fields = environment.Where(e => e.Field is not null).ToDictionary(e => e.Field!, e => Redaction.Apply(e.Value, sensitive));
        var url = IssueUrl(text, fields, MaxUrlLength, out var fits);

        Console.WriteLine("Parity bug report — collected on this machine; nothing has been sent.");
        Console.WriteLine("Read it through, then use the link to open a pre-filled issue (you can still edit everything there).");
        Console.WriteLine("URLs, routes, selectors, node names, Figma keys, tokens, file paths and environment variable values are never included.");
        Console.WriteLine();
        Console.Write(text);
        Console.WriteLine();
        Console.WriteLine("Open a pre-filled bug report:");
        Console.WriteLine(url);
        if (!fits)
            Console.WriteLine("(The full report is too long for a link — the link carries the version and environment fields only. " +
                "Paste the text above into the \"Output of parity bug-report\" field.)");
        if (opts.ContainsKey("--open"))
            Console.WriteLine(OpenInBrowser(url) ? "Opened in your browser — nothing is submitted until you press the button there."
                : "Could not open a browser; copy the link above instead.");
        else
            Console.WriteLine("Nothing was opened or sent. Pass --open to open the link in your browser.");
        return 0;
    }

    /// <summary>
    /// 錯誤回報範本的預填網址(GitHub issue 表單吃「欄位 id=值」的 query)。整段報告放進
    /// <c>bug-report</c> 欄位;網址超過 <paramref name="maxLength"/> 時只帶摘要欄位,<paramref name="fits"/> = false。
    /// </summary>
    internal static string IssueUrl(string text, IReadOnlyDictionary<string, string> fields, int maxLength, out bool fits)
    {
        var summary = new StringBuilder(FeedbackLinks.Bug);
        foreach (var (id, value) in fields) summary.Append('&').Append(id).Append('=').Append(Uri.EscapeDataString(value));
        var full = summary + "&bug-report=" + Uri.EscapeDataString(text);
        fits = full.Length <= maxLength;
        if (fits) return full;
        var shortUrl = summary.ToString();
        return shortUrl.Length <= maxLength ? shortUrl : FeedbackLinks.Bug;
    }

    /// <summary>設定檔裡所有自由字串(網址、route、frame、key、token、路徑、selector)——輸出裡出現就遮掉。</summary>
    private static IEnumerable<string> SensitiveValuesOf(ParityConfig config)
    {
        string?[] top = [config.FigmaFileKey, config.DesignFile, config.DesignImage, config.DesignToken, config.MapFile,
            config.TokensFile, config.BaselineFile, config.BaseDirectory];
        foreach (var value in top.Concat(config.Ignore))
            if (!string.IsNullOrEmpty(value)) yield return value;
        foreach (var t in config.Targets)
            foreach (var value in new[] { t.Route, t.Frame, t.Url })
                if (value.Length > 1) yield return value;
    }

    /// <summary>環境資訊。Field 是錯誤回報範本裡對應的欄位 id(預填用)。</summary>
    private static List<(string Label, string Value, string? Field)> CollectEnvironment()
    {
        var playwright = PlaywrightInfo();
        return
        [
            ("Parity", VersionCommand.Describe(), "version"),
            ("OS", $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}".Trim(), "os"),
            (".NET runtime", RuntimeInformation.FrameworkDescription, null),
            ("Install channel", InstallChannel(), null),
            ("Node", NodeVersion(), "node"),
            ("Playwright", playwright.Version, null),
            ("Chromium", playwright.Chromium, "chromium"),
            ("GitHub Actions", Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? "yes" : "no", null),
        ];
    }

    /// <summary>從執行檔的位置判斷安裝通路(位置本身不印)。</summary>
    private static string InstallChannel()
    {
        var baseDir = AppContext.BaseDirectory.Replace('\\', '/');
        if (baseDir.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase)) return "npm (npx cornhsu-parity)";
        if (baseDir.Contains("/.store/cornhsu.parity/", StringComparison.OrdinalIgnoreCase)) return "dotnet tool (NuGet)";
        if (VersionCommand.Describe().StartsWith("0.0.0-dev", StringComparison.Ordinal)) return "built from source";
        return "unknown";
    }

    /// <summary>
    /// Playwright driver 實際用的 Node:npm 通路由啟動腳本設 PLAYWRIGHT_NODEJS_PATH(使用者自己的 Node);
    /// dotnet tool 用套件附的那一份。在本機跑 <c>node --version</c>,不連網。
    /// </summary>
    private static string NodeVersion()
    {
        var candidates = new List<(string Exe, string Source)>();
        if (Environment.GetEnvironmentVariable("PLAYWRIGHT_NODEJS_PATH") is { Length: > 0 } custom)
            candidates.Add((custom, "used by Playwright (PLAYWRIGHT_NODEJS_PATH)"));
        var bundled = Path.Combine(AppContext.BaseDirectory, ".playwright", "node");
        if (Directory.Exists(bundled))
            foreach (var dir in Directory.EnumerateDirectories(bundled).Where(d => Path.GetFileName(d).Contains(BundledNodeRid(), StringComparison.Ordinal)))
                candidates.Add((Path.Combine(dir, OperatingSystem.IsWindows() ? "node.exe" : "node"), "bundled with this package"));
        candidates.Add(("node", "on PATH"));
        // 只收版本字串的形狀(v20、v20.11.1):PLAYWRIGHT_NODEJS_PATH 可以指向任何東西,它印什麼都不能原樣進報告
        foreach (var (exe, source) in candidates)
            if (Run(exe, "--version") is { } output)
                return NodeVersionShape().IsMatch(output) ? $"{output} ({source})" : $"unrecognized output ({source})";
        return "not found";
    }

    private static string BundledNodeRid()
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        return OperatingSystem.IsWindows() ? $"win32_{arch}" : OperatingSystem.IsMacOS() ? $"darwin-{arch}" : $"linux-{arch}";
    }

    private static (string Version, string Chromium) PlaywrightInfo()
    {
        // Microsoft.Playwright 組件自稱 1.0.0;真正的版本看隨套件附的 driver(.playwright/package/package.json)
        var version = "unknown";
        try
        {
            using var pkg = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "package.json")));
            var raw = pkg.RootElement.GetProperty("version").GetString() ?? "";
            version = PackageVersionShape().IsMatch(raw) ? raw : "unrecognized";
        }
        catch (Exception)
        {
        }

        string? expected = null;
        var browsersJson = Path.Combine(AppContext.BaseDirectory, ".playwright", "package", "browsers.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(browsersJson));
            var raw = doc.RootElement.GetProperty("browsers").EnumerateArray()
                .FirstOrDefault(b => b.GetProperty("name").GetString() == "chromium").GetProperty("revision").GetString();
            expected = raw is not null && RevisionShape().IsMatch(raw) ? raw : null;
        }
        catch (Exception)
        {
        }

        // 目錄名只收數字 revision;其他名字(使用者自己放的資料夾)只計數,不印
        var dirs = InstalledChromiumRevisions();
        var installed = dirs.Where(d => RevisionShape().IsMatch(d)).ToList();
        var unrecognized = dirs.Count - installed.Count;
        var list = (installed.Count == 0 ? "none" : string.Join(", ", installed)) + (unrecognized > 0 ? $"; {unrecognized} unrecognized" : "");
        var custom = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") is { Length: > 0 } ? "; a custom Playwright browser directory is configured" : "";
        var chromium = expected is null
            ? $"expected revision unknown; installed: {list}{custom}"
            : installed.Contains(expected)
                ? $"revision {expected} installed{custom}"
                : $"revision {expected} NOT installed (installed: {list}) — run `parity install-browser`{custom}";
        return (version, chromium);
    }

    private static List<string> InstalledChromiumRevisions()
    {
        var root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") is { Length: > 0 } custom && custom != "0"
            ? custom
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright")
                : OperatingSystem.IsMacOS()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "ms-playwright")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright");
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateDirectories(root, "chromium-*").Select(d => Path.GetFileName(d)["chromium-".Length..])
                    .Order(StringComparer.Ordinal).ToList()
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^v\d+(\.\d+)*$")]
    private static partial System.Text.RegularExpressions.Regex NodeVersionShape();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+$")]
    private static partial System.Text.RegularExpressions.Regex RevisionShape();

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial System.Text.RegularExpressions.Regex PackageVersionShape();

    /// <summary>在本機跑一個指令取第一行輸出(5 秒逾時);找不到或失敗回 null。</summary>
    private static string? Run(string exe, string arg)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5_000))
            {
                p.Kill(entireProcessTree: true);
                return null;
            }
            return p.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim().Split('\n')[0].Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
