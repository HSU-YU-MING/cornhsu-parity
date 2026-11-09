using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Parity.Engine;
using Parity.Storage;

namespace Parity.Cli;

/// <summary>
/// <c>parity mcp</c> 的三個工具(2.5.0,實驗性;競品清單「MCP server」)。AI agent 改完介面可以自己驗:
/// 不必由 agent 帶設計資料(DesignLatch 的做法),直接跑設定檔裡的 <c>parity check</c>。
/// 回應照 DesignLatch 值得學的那一點:不只給結果,還講下一步(<c>next</c>),清單只列前幾筆,其餘用 get_report 翻頁。
/// 結果同時放進 structuredContent 與一個 text 區塊(同一份 JSON,規格建議這樣相容舊用戶端)。
/// 不送匿名統計:agent 改一次跑一次,同一個專案會被重複計數。
/// </summary>
internal sealed class McpTools(string? defaultConfig, string workingDirectory)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // stdio 不是 HTML:中文圖層名照原樣,不跳脫成 \uXXXX(agent 讀得懂、也少花 token)
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal const string ListTargets = "parity_list_targets";
    internal const string Check = "parity_check";
    internal const string GetReport = "parity_get_report";
    internal static readonly string[] Names = [ListTargets, Check, GetReport];

    /// <summary>gaps 清單的預設與上限筆數。</summary>
    internal const int DefaultLimit = 20, MaxLimit = 200;

    /// <summary>期望/實際值、selector 在結果裡最多幾個字元(文字比對的值可能很長)。</summary>
    private const int MaxValue = 200;

    // 同一時間只跑一次掃描:兩個 check 同時開瀏覽器,誰都跑不快,報告檔也會互相蓋掉
    private static readonly SemaphoreSlim ScanLock = new(1, 1);

    private static JsonObject ConfigArg() => new()
    {
        ["type"] = "string",
        ["description"] = "Path to parity.config.json (relative to the server's working directory). Omit to use the server's " +
            "--config, or the nearest parity.config.json above the working directory.",
    };

    internal static JsonArray Definitions() =>
    [
        new JsonObject
        {
            ["name"] = ListTargets,
            ["title"] = "List Parity targets",
            ["description"] = "List the pages (targets) Parity checks, where the design comes from, the gate settings, and " +
                "whether a report from an earlier check exists. Fast: opens no browser.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["config"] = ConfigArg() },
                ["additionalProperties"] = false,
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
        },
        new JsonObject
        {
            ["name"] = Check,
            ["title"] = "Check the page against the design",
            ["description"] = "Run `parity check`: render each target page in a headless browser, match it to the design and " +
                "compare real values (size, spacing, color, type…). Returns the verdict (pass / fail / untrusted, with the same " +
                "exit code `parity check` would give), the fidelity score, the most important gaps with a suggested CSS fix, and " +
                "what to do next. Writes .parity/report.json like the CLI. Takes seconds to a minute; run it after changing the UI.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["config"] = ConfigArg(),
                    ["target"] = new JsonObject { ["type"] = "string", ["description"] = "Only check the target with this route (e.g. \"/pricing\")." },
                    ["baseline"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Regression mode, like `parity check --baseline`: only gaps that are new or worse than the saved " +
                            "baseline fail. Falls back to the normal gate when there is no baseline yet.",
                    },
                    ["limit"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxLimit,
                        ["description"] = $"How many gaps to list (default {DefaultLimit}). The rest: parity_get_report.",
                    },
                    ["includeSoft"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Also list soft gaps (reported, never gated: font family, text, images…). Default false.",
                    },
                },
                ["additionalProperties"] = false,
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = false, ["idempotentHint"] = true, ["openWorldHint"] = true },
        },
        new JsonObject
        {
            ["name"] = GetReport,
            ["title"] = "Read the last Parity report",
            ["description"] = "Page through the gaps in the report the last check wrote (.parity/report.json), without re-running. " +
                "Filter by route, property or minimum severity.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["config"] = ConfigArg(),
                    ["route"] = new JsonObject { ["type"] = "string", ["description"] = "Only gaps on this target route." },
                    ["prop"] = new JsonObject { ["type"] = "string", ["description"] = "Only gaps in this property (e.g. \"paddingTop\")." },
                    ["minSeverity"] = new JsonObject
                    {
                        ["type"] = "string", ["enum"] = new JsonArray("minor", "medium", "serious", "critical"),
                        ["description"] = "Only gaps at least this severe.",
                    },
                    ["includeSoft"] = new JsonObject { ["type"] = "boolean", ["description"] = "Also list soft gaps. Default false." },
                    ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Skip this many gaps (default 0)." },
                    ["limit"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1, ["maximum"] = MaxLimit,
                        ["description"] = $"How many gaps to list (default {DefaultLimit}).",
                    },
                },
                ["additionalProperties"] = false,
            },
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true, ["openWorldHint"] = false },
        },
    ];

    /// <summary>跑一個工具。參數值不對、設定檔壞了、瀏覽器沒裝……都回 isError 結果(附下一步),不是協定錯誤。</summary>
    public async Task<JsonObject> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        try
        {
            var result = name switch
            {
                ListTargets => RunListTargets(new Args(args, "config")),
                Check => await RunCheckAsync(new Args(args, "config", "target", "baseline", "limit", "includeSoft"), ct),
                GetReport => RunGetReport(new Args(args, "config", "route", "prop", "minSeverity", "includeSoft", "offset", "limit")),
                _ => throw new McpProtocolException(-32602, $"Unknown tool: {name}"),
            };
            return Result(result, isError: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result(new JsonObject { ["error"] = ex.Message, ["next"] = NextAfterError(ex) }, isError: true);
        }
    }

    private static string NextAfterError(Exception ex) => ex switch
    {
        _ when ex.Message.Contains("install-browser", StringComparison.OrdinalIgnoreCase) =>
            "The browser Parity uses is not installed. Ask the user to run `parity install-browser` in a terminal, then call the tool again.",
        ToolArgumentException => "Fix the argument and call the tool again.",
        FileNotFoundException when ex.Message.Contains("report not found", StringComparison.Ordinal) =>
            "There is no report yet: call parity_check first.",
        FileNotFoundException => "Pass the config path, or create one with `parity init` in the project folder.",
        _ => "Read the error: it usually names the config field or file to fix. Configuration errors are the user's to decide on.",
    };

    private static JsonObject Result(JsonObject payload, bool isError)
    {
        Prune(payload);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString(Json) }),
            ["structuredContent"] = payload,
            ["isError"] = isError,
        };
    }

    /// <summary>值是 null 的欄位整個拿掉(JsonObject 不吃 WhenWritingNull):沒有就是沒有,少給 agent 讀一堆 null。</summary>
    internal static void Prune(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Where(p => p.Value is null).Select(p => p.Key).ToList()) o.Remove(key);
                foreach (var (_, value) in o) Prune(value);
                break;
            case JsonArray a:
                foreach (var item in a) Prune(item);
                break;
        }
    }

    // ---------- parity_list_targets ----------

    private JsonObject RunListTargets(Args args)
    {
        var (path, config) = LoadConfig(args.String("config"));
        var report = ReportPath(config);
        return new JsonObject
        {
            ["config"] = path,
            ["designSource"] = UsageStats.DesignSourceOf(config),
            ["targets"] = new JsonArray([.. config.Targets.Select(t => (JsonNode)new JsonObject
            {
                ["route"] = t.Route, ["url"] = t.Url, ["frame"] = t.Frame, ["width"] = t.Width, ["height"] = t.Height,
            })]),
            ["gate"] = config.GateCriteria(),
            ["acceptRules"] = config.Accept.Count,
            ["lastReport"] = File.Exists(report)
                ? new JsonObject { ["path"] = report, ["writtenAt"] = File.GetLastWriteTimeUtc(report).ToString("o") }
                : null,
            ["next"] = config.Targets.Count == 0
                ? "The config declares no targets: ask the user which page and design frame to compare."
                : "Call parity_check (optionally with target) to compare the page with the design.",
        };
    }

    // ---------- parity_check ----------

    private async Task<JsonObject> RunCheckAsync(Args args, CancellationToken ct)
    {
        var configPath = ResolveConfigPath(args.String("config"));
        var target = args.String("target");
        var useBaseline = args.Bool("baseline") ?? false;
        var limit = args.Int("limit", 1, MaxLimit) ?? DefaultLimit;
        var includeSoft = args.Bool("includeSoft") ?? false;

        await ScanLock.WaitAsync(ct);
        try
        {
            await using var session = new ScanSession(configPath);
            var scans = await session.RunAsync(target, ct);
            var reports = scans.Select(s => s.Result.Report).ToList();
            var config = session.Config;

            var reportPath = ReportPath(config);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(ReportDocument.Of(reports), ReportJson.Indented), ct);

            var tokens = TokensFile.LoadForHints(config);
            var result = new JsonObject();
            var integrity = config.MatchIntegrityFailures(reports);
            string verdict;
            List<string> reasons;
            JsonObject? baselineNode = null;
            if (integrity.Count > 0)
                (verdict, reasons) = ("untrusted", integrity);
            else if (useBaseline && await LatestBaselineAsync(config) is { } baseline)
            {
                var cmp = BaselineComparer.Compare(DiffRecord.FromReports(reports), baseline.Diffs, DiffRecord.KeysOf(reports));
                verdict = cmp.HasRegressions ? "fail" : "pass";
                reasons = cmp.HasRegressions ? [$"{cmp.Regressions.Count} new and {cmp.Worsened.Count} worsened gap(s) vs the baseline"] : [];
                baselineNode = new JsonObject
                {
                    ["new"] = Records(cmp.Regressions, limit, tokens),
                    ["worsened"] = Records(cmp.Worsened, limit, tokens),
                    ["fixed"] = cmp.Fixed.Count,
                    ["unchanged"] = cmp.Unchanged,
                    ["baselineScore"] = baseline.Score,
                };
            }
            else
            {
                reasons = config.GateFailReasons(reports);
                verdict = reasons.Count > 0 ? "fail" : "pass";
                if (useBaseline) baselineNode = new JsonObject { ["note"] = "no baseline yet (run `parity baseline save`); the normal gate was used" };
            }

            var gaps = Gaps(reports, includeSoft, tokens);
            result["verdict"] = verdict;
            result["exitCode"] = verdict switch { "untrusted" => 3, "fail" => 1, _ => 0 };
            result["reasons"] = new JsonArray([.. reasons.Select(r => (JsonNode)r)]);
            result["score"] = FidelityScore.Compute(reports);
            result["pages"] = new JsonArray([.. reports.Select(Page)]);
            result["gaps"] = Listing(gaps, 0, limit);
            result["softGaps"] = reports.Sum(r => r.Nodes.Sum(n => n.Diffs.Count(d => d.Soft)));
            result["unmatched"] = Unmatched(reports, limit);
            if (reports.Any(r => r.Extras is not null))
                result["extras"] = Extras(reports);
            if (baselineNode is not null) result["baseline"] = baselineNode;
            result["report"] = reportPath;
            result["next"] = NextAfterCheck(verdict, reports, gaps.Count, limit, baselineNode is not null && baselineNode["note"] is null);
            return result;
        }
        finally
        {
            ScanLock.Release();
        }
    }

    private static async Task<LatestBaseline?> LatestBaselineAsync(ParityConfig config)
    {
        // db 不存在就是還沒有 baseline:不開 BaselineStore(建構子會建出空 db 留在 repo 裡)
        var db = BaselineCommand.BaselineDbPath(config);
        if (!File.Exists(db)) return null;
        await using var store = new BaselineStore(db);
        return await store.GetLatestAsync();
    }

    private static string NextAfterCheck(string verdict, List<FidelityReport> reports, int gapCount, int limit, bool baselineMode)
    {
        var parts = new List<string>();
        if (reports.Any(r => r.FontsNotLoaded is { Count: > 0 }))
            parts.Add("Some web fonts did not load (pages[].fontsNotLoaded): text was measured with a fallback font, so fix font loading first — " +
                "many text gaps may come from that alone.");
        parts.Add(verdict switch
        {
            "untrusted" => "Too few design nodes matched to trust the result — usually the target url or frame is wrong, the page did not " +
                "load, or it needs a login (the user can run `parity login` and set auth.storageState) or a menu or tab opened first " +
                "(targets[].steps). Check pages[] and the config before fixing any gap.",
            "fail" when baselineMode => "Fix the new and worsened gaps under baseline (each has a suggested fix), then call parity_check again.",
            "fail" => "Fix the gaps listed, most severe first (each has a suggested CSS fix), then call parity_check again. If a gap is a " +
                "deliberate design decision, the user can accept it with an `accept` entry and a reason in parity.config.json — do not add " +
                "one on your own.",
            _ => "The page matches the design within tolerance.",
        });
        if (gapCount > limit)
            parts.Add($"Only {limit} of {gapCount} gaps are listed: use parity_get_report with offset to see the rest.");
        return string.Join(" ", parts);
    }

    // ---------- parity_get_report ----------

    private JsonObject RunGetReport(Args args)
    {
        var (_, config) = LoadConfig(args.String("config"));
        var route = args.String("route");
        var prop = args.String("prop");
        var minSeverity = args.String("minSeverity") is { } s
            ? Enum.TryParse<Severity>(s, ignoreCase: true, out var sev) && sev != Severity.None ? sev
                : throw new ToolArgumentException($"minSeverity must be one of minor, medium, serious, critical (got \"{s}\")")
            : Severity.None;
        var includeSoft = args.Bool("includeSoft") ?? false;
        var offset = args.Int("offset", 0, int.MaxValue) ?? 0;
        var limit = args.Int("limit", 1, MaxLimit) ?? DefaultLimit;

        var path = ReportPath(config);
        if (!File.Exists(path)) throw new FileNotFoundException($"report not found: {path}", path);
        var doc = ReportCommand.LoadReport(path);
        var reports = doc.Reports.Where(r => route is null || r.Route == route).ToList();
        var gaps = Gaps(reports, includeSoft, TokensFile.LoadForHints(config))
            .Where(g => (prop is null || (string?)g["prop"] == prop) && SeverityOf(g) >= minSeverity)
            .ToList();

        var result = new JsonObject
        {
            ["report"] = path,
            ["writtenAt"] = File.GetLastWriteTimeUtc(path).ToString("o"),
            ["score"] = FidelityScore.Compute(doc.Reports),
            ["gaps"] = Listing(gaps, offset, limit),
        };
        if (doc.Direction == ReportDirection.Reverse)
            result["note"] = "This report was written by `parity check --reverse`: expected is the page and actual is the design.";
        if (route is not null && reports.Count == 0)
            result["next"] = $"The report has no target with route \"{route}\". Routes: {string.Join(", ", doc.Reports.Select(r => r.Route))}.";
        else if (offset + limit < gaps.Count)
            result["next"] = $"Call again with offset {offset + limit} for the next gaps.";
        return result;
    }

    // ---------- 共用:設定檔、清單 ----------

    private string ResolveConfigPath(string? arg)
    {
        if (arg is not null) return Path.GetFullPath(Path.Combine(workingDirectory, arg));
        if (defaultConfig is not null) return Path.GetFullPath(Path.Combine(workingDirectory, defaultConfig));
        return ParityConfig.FindConfigFile(workingDirectory)
            ?? throw new FileNotFoundException("parity.config.json not found above the server's working directory.");
    }

    private (string Path, ParityConfig Config) LoadConfig(string? arg)
    {
        var path = ResolveConfigPath(arg);
        return (path, ParityConfig.Load(path));
    }

    private static string ReportPath(ParityConfig config) => Path.Combine(config.BaseDirectory, ".parity", "report.json");

    private static JsonNode Page(FidelityReport r) => new JsonObject
    {
        ["route"] = r.Route,
        ["url"] = r.Url,
        ["designNodes"] = r.Summary.DesignNodes,
        ["matched"] = r.Summary.Matched,
        ["unmatched"] = r.Summary.Unmatched,
        ["gaps"] = r.Nodes.Sum(n => n.Diffs.Count(d => !d.Soft)),
        ["fontsNotLoaded"] = r.FontsNotLoaded is { Count: > 0 } f ? new JsonArray([.. f.Select(x => (JsonNode)x)]) : null,
    };

    /// <summary>落差清單:會擋的(非 soft)在前;同一類裡照報告的衝擊度(嚴重度、元素面積)排。</summary>
    private static List<JsonObject> Gaps(IEnumerable<FidelityReport> reports, bool includeSoft, DesignTokens? tokens)
        => reports
            .SelectMany(r => Impact.Order(r.Nodes).SelectMany(n => n.Diffs
                .Where(d => includeSoft || !d.Soft)
                .Select(d => (r.Route, Node: n, Diff: d))))
            .OrderBy(x => x.Diff.Soft)
            .ThenByDescending(x => (int)x.Diff.Severity)
            .ThenByDescending(x => Impact.Area(x.Node))
            .Select(x => Item(x.Route, x.Node.DesignLayer, x.Node.Selector, x.Diff, tokens))
            .ToList();

    private static JsonObject Item(string route, string layer, string selector, PropDiff d, DesignTokens? tokens) => new()
    {
        ["route"] = route,
        ["layer"] = layer,
        ["selector"] = Clip(selector),
        ["prop"] = d.Prop,
        ["expected"] = Clip(d.Expected),
        ["actual"] = Clip(d.Actual),
        ["unit"] = d.Unit,
        ["severity"] = d.Severity.ToString().ToLowerInvariant(),
        ["soft"] = d.Soft ? true : null,
        ["accepted"] = d.Accepted,
        ["fix"] = FixHint.For(d, tokens),
    };

    private static JsonArray Records(IEnumerable<DiffRecord> records, int limit, DesignTokens? tokens)
        => new([.. records.Take(limit).Select(r => (JsonNode)Item(r.Route, r.DesignLayer, r.Selector,
            new PropDiff(r.Prop, r.Expected, r.Actual, null, null, 0, r.Severity), tokens))]);

    private static Severity SeverityOf(JsonObject gap)
        => Enum.Parse<Severity>((string)gap["severity"]!, ignoreCase: true);

    private static JsonObject Listing(List<JsonObject> items, int offset, int limit) => new()
    {
        ["total"] = items.Count,
        ["offset"] = offset == 0 ? null : offset,
        ["items"] = new JsonArray([.. items.Skip(offset).Take(limit).Select(i => (JsonNode)i.DeepClone())]),
    };

    private static JsonObject Unmatched(List<FidelityReport> reports, int limit)
    {
        var all = reports.SelectMany(r => r.Unmatched.Select(u => (r.Route, u))).ToList();
        return new JsonObject
        {
            ["total"] = all.Count,
            ["items"] = new JsonArray([.. all.Take(limit).Select(x => (JsonNode)new JsonObject
            {
                ["route"] = x.Route, ["layer"] = x.u.DesignLayer, ["reason"] = x.u.Reason,
            })]),
        };
    }

    private static JsonObject Extras(List<FidelityReport> reports)
    {
        var listed = reports.Where(r => r.Extras is not null).SelectMany(r => r.Extras!.Items.Select(e => (r.Route, e))).Take(10);
        return new JsonObject
        {
            ["total"] = reports.Sum(r => r.Extras?.Total ?? 0),
            ["items"] = new JsonArray([.. listed.Select(x => (JsonNode)new JsonObject
            {
                ["route"] = x.Route, ["selector"] = Clip(x.e.Selector), ["tag"] = x.e.Tag, ["box"] = ExtraBox.Describe(x.e.Box),
            })]),
        };
    }

    private static string Clip(string s) => s.Length <= MaxValue ? s : s[..(MaxValue - 1)] + "…";

    /// <summary>工具參數:型別不對、值超出範圍、不認得的參數名,都是 <see cref="ToolArgumentException"/>(回 isError,agent 改了再呼叫)。</summary>
    private sealed class Args
    {
        private readonly JsonObject _args;

        public Args(JsonObject args, params string[] known)
        {
            _args = args;
            foreach (var key in args.Select(p => p.Key))
                if (!known.Contains(key))
                    throw new ToolArgumentException($"unknown argument \"{key}\" (this tool takes: {string.Join(", ", known)})");
        }

        public string? String(string key) => _args[key] switch
        {
            null => null,
            JsonValue v when v.GetValueKind() == JsonValueKind.String => (string)v!,
            _ => throw new ToolArgumentException($"{key} must be a string"),
        };

        public bool? Bool(string key) => _args[key] switch
        {
            null => null,
            JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => (bool)v!,
            _ => throw new ToolArgumentException($"{key} must be true or false"),
        };

        public int? Int(string key, int min, int max)
        {
            if (_args[key] is null) return null;
            if (_args[key] is not JsonValue v || v.GetValueKind() != JsonValueKind.Number || !v.TryGetValue<double>(out var d) || d != Math.Floor(d))
                throw new ToolArgumentException($"{key} must be an integer");
            if (d < min || d > max) throw new ToolArgumentException($"{key} must be between {min} and {max} (got {d})");
            return (int)d;
        }
    }
}

internal sealed class ToolArgumentException(string message) : Exception(message);
