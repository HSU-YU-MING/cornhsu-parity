using System.Reflection;
using Parity.Cli;

namespace Parity.Tests;

/// <summary>
/// 動到「整個行程共用的狀態」的測試——Console.Out/Error、環境變數、目前目錄——都放這個集合。
/// DisableParallelization:xunit 會等所有平行集合跑完,才單獨跑這一批,彼此不會互相污染
/// (例如 CI 上的 GITHUB_SHA 被別條測試改掉、或別的測試的輸出混進攔截到的 stdout)。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStateCollection
{
    public const string Name = "process-global state (console / env / cwd)";
}

/// <summary>
/// 在行程內跑 parity 的**真正進入點**(top-level Program:指令分派 + 例外 → exit 2 的那層),
/// 攔下 stdout/stderr 與 exit code。只能在 <see cref="ProcessStateCollection"/> 裡用。
/// </summary>
internal static class Cli
{
    public sealed record Result(int ExitCode, string Out, string Err);

    private static readonly MethodInfo EntryPoint = typeof(CliOptions).Assembly.EntryPoint
        ?? throw new InvalidOperationException("parity.dll has no entry point");

    private static readonly MethodInfo ServerEntryPoint = typeof(ParityServerEntryPoint).Assembly.EntryPoint
        ?? throw new InvalidOperationException("Parity.Server.dll has no entry point");

    public static Task<Result> RunAsync(params string[] args) => InvokeAsync(EntryPoint, args);

    /// <summary>
    /// Parity.Server 的管理指令模式(create-project / create-invite / backup)。只能傳管理指令——
    /// 其他參數會走到 web host 的 app.Run(),一直跑下去。
    /// </summary>
    public static Task<Result> RunServerAdminAsync(params string[] args)
    {
        if (args.Length == 0 || args[0] is not ("create-project" or "create-invite" or "backup"))
            throw new ArgumentException("only admin commands; anything else starts the web host", nameof(args));
        return InvokeAsync(ServerEntryPoint, args);
    }

    private static async Task<Result> InvokeAsync(MethodInfo entryPoint, string[] args)
    {
        var (prevOut, prevErr) = (Console.Out, Console.Error);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            // 丟到 thread pool 跑:合成的 Main 內部是同步等待(GetAwaiter().GetResult()),
            // 直接在 xunit 的 SynchronizationContext 上呼叫,執行緒少的機器上會自己卡死自己
            var returned = await Task.Run(() => entryPoint.Invoke(null, [args]));
            var code = returned is Task<int> task ? await task : (int)returned!;
            return new Result(code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    /// <summary>CLI 層級的「沒裝瀏覽器」:進入點把例外印成 error 並回 2。</summary>
    public static bool BrowserMissing(Result r)
        => r.ExitCode == 2 && r.Err.Contains("install-browser", StringComparison.OrdinalIgnoreCase);
}

/// <summary>暫時改環境變數,Dispose 時逐一還原(含「原本不存在」)。</summary>
internal sealed class EnvScope : IDisposable
{
    // parity push 會讀的 CI 環境變數。GitHub Actions 上跑測試時它們全都有值——不清掉的話,
    // 測試結果會隨「在哪裡跑」而變
    public static readonly string[] PushVariables =
    [
        "PARITY_SERVER", "PARITY_TOKEN", "GITHUB_SHA", "GITHUB_HEAD_REF", "GITHUB_REF_NAME",
        "GITHUB_ACTOR", "GITHUB_SERVER_URL", "GITHUB_REPOSITORY", "GITHUB_STEP_SUMMARY",
    ];

    // 匿名統計(2.1.0)會讀的變數。清掉之後 DO_NOT_TRACK 也沒了——統計會打開,所以清它的測試一定要
    // 把 CORNHSU_STATS_URL 指到本機假站(TestProcessDefaults 另外只准送往 loopback)。CI/GITHUB_ACTIONS 在
    // GitHub Actions 上都是 true,不清的話「本機第一次」的說明在 CI 上永遠測不到
    public static readonly string[] StatsVariables =
    [
        "DO_NOT_TRACK", "CORNHSU_SHARE_STATS", "PARITY_SHARE_STATS", "CORNHSU_STATS_URL",
        "CI", "GITHUB_ACTIONS", "PARITY_STATS_JOB_SUMMARY", "GITHUB_STEP_SUMMARY",
    ];

    private readonly Dictionary<string, string?> _saved = [];

    public EnvScope Set(string name, string? value)
    {
        if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public EnvScope Clear(IEnumerable<string> names)
    {
        foreach (var n in names) Set(n, null);
        return this;
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>暫時切換目前目錄(init 寫在 cwd、找設定檔從 cwd 往上找)。</summary>
internal sealed class CwdScope : IDisposable
{
    private readonly string _previous = Directory.GetCurrentDirectory();

    public CwdScope(string directory) => Directory.SetCurrentDirectory(directory);

    public void Dispose() => Directory.SetCurrentDirectory(_previous);
}

/// <summary>每條測試一個獨立暫存目錄,結束整個刪掉。</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"parity-test-{Guid.NewGuid():N}");

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relative, string content)
    {
        var full = File(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // baseline db 的檔案 handle 先放掉
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* 殘檔在 %TEMP%,無害 */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>repo 內的檔案(README、action.yml、samples…):從測試輸出目錄往上找 Parity.slnx。</summary>
internal static class Repo
{
    public static string Root { get; } = FindRoot();

    public static string PathOf(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public static string Read(params string[] parts) => File.ReadAllText(PathOf(parts));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Parity.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("Parity.slnx not found above the test output directory");
    }
}
