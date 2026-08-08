using System.Diagnostics;
using Parity.Engine;
using Parity.Storage;

namespace Parity.Cli;

/// <summary>parity baseline:把當前落差存成基準 / 看歷史(規畫書 M5)。搭配 `check --baseline` 做回歸把關。</summary>
internal static class BaselineCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Skip(1).ToArray();
        return sub switch
        {
            "save" => await SaveAsync(rest),
            "list" => await ListAsync(rest),
            "help" or "--help" or "-h" => Help(),
            _ => UnknownSub(sub),
        };
    }

    /// <summary>跑一次掃描,把當前落差存成新的 baseline 快照。</summary>
    private static async Task<int> SaveAsync(string[] args)
    {
        var opts = CliOptions.Parse(args, "--config=", "--target=", "--headed");
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.Baseline);
        var configPath = ResolveConfig(opts);

        await using var session = new ScanSession(configPath, headless: !opts.ContainsKey("--headed"));
        var scans = await session.RunAsync(opts.GetValueOrDefault("--target"));

        // 配對不可信(0 配對等)時拒存:存下去的會是「0 條落差」的空基準,
        // 之後設定修好,所有真實落差全變「新增」——比不存更糟。
        var integrity = session.MatchIntegrityFailures(scans);
        if (integrity.Count > 0)
        {
            Console.Error.WriteLine("\x1b[31m✘ refusing to save baseline: match integrity too low (the baseline would be incomplete)\x1b[0m");
            foreach (var r in integrity)
                Console.Error.WriteLine($"  \x1b[31m·\x1b[0m {r}");
            return 2;
        }

        var reports = scans.Select(s => s.Result.Report).ToList();
        var diffs = DiffRecord.FromReports(reports);
        var score = FidelityScore.Compute(reports);

        var (commit, branch) = GitInfo.TryRead(session.Config.BaseDirectory);
        await using var store = new BaselineStore(BaselineDbPath(session.Config));
        var id = await store.SaveAsync(diffs, DateTime.UtcNow, commit, branch, score);

        var at = commit is null ? "" : $" @ {Short(commit)}{(branch is null ? "" : $" ({branch})")}";
        Console.WriteLine($"\x1b[32m✔\x1b[0m saved baseline #{id}: {diffs.Count} diff(s) · fidelity {score}/100{at}");
        Console.WriteLine("From now on `parity check --baseline` only gates on diffs that are new or worse than this baseline.");
        return 0;
    }

    /// <summary>列出歷史 baseline 快照(新到舊)。</summary>
    private static async Task<int> ListAsync(string[] args)
    {
        var opts = CliOptions.Parse(args, "--config=");
        if (opts.ContainsKey("--help")) return Usage.Print(Usage.Baseline);
        var configPath = ResolveConfig(opts);
        var config = ParityConfig.Load(configPath);

        await using var store = new BaselineStore(BaselineDbPath(config));
        var history = await store.HistoryAsync();
        if (history.Count == 0)
        {
            Console.WriteLine("No baseline yet. Run `parity baseline save` first.");
            return 0;
        }
        Console.WriteLine("\x1b[1mbaseline history\x1b[0m (newest first; the score column is the fidelity trend):");
        foreach (var (id, createdAt, commit, diffCount, score) in history)
            Console.WriteLine($"  #{id,-4} {createdAt:yyyy-MM-dd HH:mm}  {diffCount,3} diffs  {(score is { } s ? $"{s,3}/100" : "  —  ")}" +
                (commit is null ? "" : $"  @ {Short(commit)}"));
        return 0;
    }

    private static int UnknownSub(string sub)
    {
        Console.Error.WriteLine($"unknown `baseline` subcommand: \"{sub}\"\n");
        Help();
        return 2;
    }

    private static int Help()
    {
        Console.WriteLine("""
            parity baseline — diff baselines and their history

            Usage:
              parity baseline save [--config <path>] [--target <route>]
                  Run one scan and store the current diffs as a new baseline snapshot
              parity baseline list [--config <path>]
                  List the stored baseline snapshots

            Pairs with: parity check --baseline
                  Compares the current state against the newest baseline and only GATE FAILs on diffs that are
                  new or worse — which is what lets a project with existing debt adopt Parity today.
            """);
        return 0;
    }

    internal static string BaselineDbPath(ParityConfig config)
        => Path.Combine(config.BaseDirectory, config.BaselineFile ?? "parity.baseline.db");

    private static string ResolveConfig(Dictionary<string, string?> opts)
        => opts.GetValueOrDefault("--config")
           ?? ParityConfig.FindConfigFile(Directory.GetCurrentDirectory())
           ?? throw new FileNotFoundException("parity.config.json not found (run `parity init` to generate a template).");

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;
}

/// <summary>盡力讀取 git commit / branch(讀不到就回 null,不當錯誤)——給 baseline 快照標記出處。</summary>
internal static class GitInfo
{
    public static (string? Commit, string? Branch) TryRead(string workingDir)
        => (Run(workingDir, "rev-parse HEAD"), Run(workingDir, "rev-parse --abbrev-ref HEAD"));

    private static string? Run(string workingDir, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            // 兩個 stream 都要非同步排掉——只讀一個、另一個塞爆 buffer 會 deadlock
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(2000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 已結束 */ }
                return null;
            }
            _ = stderr.GetAwaiter().GetResult(); // 排掉 stderr
            var output = stdout.GetAwaiter().GetResult().Trim();
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null; // 沒裝 git / 不是 repo → 靜默略過
        }
    }
}
