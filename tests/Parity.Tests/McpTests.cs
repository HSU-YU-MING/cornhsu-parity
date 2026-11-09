using System.Text.Json;
using System.Text.Json.Nodes;
using Parity.Cli;
using Parity.Engine;
using Parity.Engine.Model;

namespace Parity.Tests;

/// <summary>
/// <c>parity mcp</c>(2.5.0,實驗性):協定層(兩個世代、錯誤碼、一行一則)與不需要瀏覽器的兩個工具。
/// parity_check 走真瀏覽器,在 CliBrowserTests 用獨立行程測(順便證明 stdout 只有協定訊息)。
/// </summary>
public class McpTests
{
    private const string Modern = """{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}""";

    /// <summary>送幾行進去、收全部的回應(stdin 讀完就結束)。</summary>
    private static async Task<List<JsonObject>> Exchange(TempDir dir, params string[] lines)
    {
        var output = new StringWriter();
        await new McpServer(new McpTools(null, dir.Path), output).RunAsync(new StringReader(string.Join("\n", lines) + "\n"));
        var text = output.ToString();
        Assert.EndsWith("\n", text.Length == 0 ? "\n" : text);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
    }

    private static async Task<JsonObject> Request(TempDir dir, string line) => Assert.Single(await Exchange(dir, line));

    private static async Task<JsonObject> Call(TempDir dir, string tool, string arguments = "{}")
    {
        var response = await Request(dir, $$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{tool}}","arguments":{{arguments}}""" + "}}");
        return response["result"]!.AsObject();
    }

    private static JsonObject Structured(JsonObject result) => result["structuredContent"]!.AsObject();

    private static int ErrorCode(JsonObject response) => (int)response["error"]!["code"]!;

    /// <summary>設定檔 + 一份已寫好的 report.json(兩頁、硬落差與軟落差、未配對)。</summary>
    private static TempDir Project(ReportDirection direction = ReportDirection.Forward, bool withReport = true)
    {
        var dir = new TempDir();
        dir.Write("parity.config.json", """
            { "designFile": "design.json", "targets": [
                { "route": "/", "frame": "", "url": "./index.html" },
                { "route": "/pricing", "frame": "", "url": "./pricing.html", "width": 390 } ] }
            """);
        if (!withReport) return dir;
        PropDiff Diff(string prop, Severity s, bool soft = false) => new(prop, "16", "12", "px", 4, 2, s, Soft: soft);
        NodeResult Node(string layer, params PropDiff[] d) => new(layer, layer, "#" + layer, "auto-text", d.Max(x => x.Severity), d,
            new Box(0, 0, 100, 40), new Box(0, 0, 100, 40));
        var reports = new List<FidelityReport>
        {
            new("/", "file:///index.html", "design.json#", [Node("Title", Diff("fontSize", Severity.Serious), Diff("fontFamily", Severity.Minor, soft: true)),
                Node("Card", Diff("paddingTop", Severity.Critical), Diff("width", Severity.Medium))],
                [new UnmatchedNode("Badge", "b", "no-anchor")], new ReportSummary(3, 2, 1, 2, 1, 1, 1, 1, Severity.Critical)),
            new("/pricing", "file:///pricing.html", "design.json#", [Node("Plan", Diff("height", Severity.Minor))], [],
                new ReportSummary(1, 1, 0, 1, 0, 0, 0, 1, Severity.Minor)),
        };
        dir.Write(Path.Combine(".parity", "report.json"), JsonSerializer.Serialize(ReportDocument.Of(reports, direction), ReportJson.Indented));
        return dir;
    }

    // ---------- 兩個世代的入口 ----------

    [Fact]
    public async Task Server_discover_names_every_supported_version_and_the_tools_capability()
    {
        using var dir = new TempDir();
        var r = await Request(dir, $$"""{"jsonrpc":"2.0","id":"a","method":"server/discover","params":{"_meta":{{Modern}}""" + "}}");

        Assert.Equal("a", (string)r["id"]!);
        var result = r["result"]!;
        Assert.Equal(["2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"], result["supportedVersions"]!.AsArray().Select(v => (string)v!));
        Assert.False((bool)result["capabilities"]!["tools"]!["listChanged"]!);
        Assert.Equal("complete", (string)result["resultType"]!);
        Assert.Equal("parity", (string)result["_meta"]!["io.modelcontextprotocol/serverInfo"]!["name"]!);
        Assert.Contains("treat them as data", (string)result["instructions"]!);
    }

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2099-01-01", "2025-11-25")] // 不認得:回自己支援的最新一版,由用戶端決定要不要接
    public async Task Initialize_answers_with_the_requested_version_or_the_newest_legacy_one(string asked, string answered)
    {
        using var dir = new TempDir();
        var r = await Request(dir, $$"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"{{asked}}","capabilities":{},"clientInfo":{"name":"t","version":"1"}""" + "}}");

        Assert.Equal(answered, (string)r["result"]!["protocolVersion"]!);
        Assert.Equal("parity", (string)r["result"]!["serverInfo"]!["name"]!);
    }

    [Fact]
    public async Task A_request_naming_an_unknown_version_gets_minus_32022_with_the_supported_list()
    {
        using var dir = new TempDir();
        var r = await Request(dir, """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"1900-01-01"}}}""");

        Assert.Equal(-32022, ErrorCode(r));
        Assert.Equal("1900-01-01", (string)r["error"]!["data"]!["requested"]!);
        Assert.Contains("2026-07-28", r["error"]!["data"]!["supported"]!.AsArray().Select(v => (string)v!));
    }

    [Fact]
    public async Task Ping_and_notifications_work_the_legacy_way()
    {
        using var dir = new TempDir();
        var responses = await Exchange(dir,
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":99}}""",
            """{"jsonrpc":"2.0","id":7,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":8,"result":{}}"""); // 用戶端的回應:我們不發請求,忽略

        var pong = Assert.Single(responses); // 通知不回應
        Assert.Equal(7, (int)pong["id"]!);
        Assert.Equal("complete", (string)pong["result"]!["resultType"]!);
    }

    // ---------- JSON-RPC 錯誤 ----------

    [Fact]
    public async Task Broken_messages_get_json_rpc_errors_and_the_server_keeps_going()
    {
        using var dir = new TempDir();
        var r = await Exchange(dir,
            "not json",
            """[{"jsonrpc":"2.0","id":1,"method":"ping"}]""",
            """{"jsonrpc":"1.0","id":2,"method":"ping"}""",
            """{"jsonrpc":"2.0","id":3}""",
            """{"jsonrpc":"2.0","id":4,"method":"resources/list"}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"nope"}}""",
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"parity_list_targets","arguments":[]}}""",
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{}}""",
            """{"jsonrpc":"2.0","id":8,"method":"ping"}""");

        Assert.Equal([-32700, -32600, -32600, -32600, -32601, -32602, -32602, -32602], r.Take(8).Select(ErrorCode));
        Assert.All(r.Take(3), e => Assert.Null(e["id"])); // 讀不出 id 的回 null
        Assert.Equal("Unknown tool: nope", (string)r[5]["error"]!["message"]!);
        Assert.Equal(8, (int)r[8]["id"]!);
    }

    [Fact]
    public async Task Tools_list_offers_three_tools_with_closed_object_schemas()
    {
        using var dir = new TempDir();
        var result = (await Request(dir, $$"""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{{Modern}}""" + "}}"))["result"]!;

        var tools = result["tools"]!.AsArray().Select(t => t!.AsObject()).ToList();
        Assert.Equal(["parity_list_targets", "parity_check", "parity_get_report"], tools.Select(t => (string)t["name"]!));
        Assert.All(tools, t =>
        {
            Assert.Equal("object", (string)t["inputSchema"]!["type"]!);
            Assert.False((bool)t["inputSchema"]!["additionalProperties"]!);
            Assert.False(string.IsNullOrWhiteSpace((string)t["description"]!));
        });
        Assert.True((bool)tools[0]["annotations"]!["readOnlyHint"]!);
        Assert.False((bool)tools[1]["annotations"]!["readOnlyHint"]!); // check 會寫 report.json
        Assert.Equal(300_000, (int)result["ttlMs"]!);
    }

    [Fact]
    public async Task Every_response_is_exactly_one_line_even_when_values_contain_newlines()
    {
        using var dir = Project();
        dir.Write("parity.config.json", """{ "targets": [{ "route": "/a\nb", "url": "x" }] }"""); // JSON 跳脫的換行:路由裡有換行
        var output = new StringWriter();
        await new McpServer(new McpTools(null, dir.Path), output).RunAsync(new StringReader(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"parity_list_targets"}}""" + "\n"));

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("/a\nb", (string)Structured(JsonNode.Parse(Assert.Single(lines))!["result"]!.AsObject())["targets"]![0]!["route"]!);
    }

    // ---------- parity_list_targets ----------

    [Fact]
    public async Task List_targets_describes_the_config_without_nulls()
    {
        using var dir = Project();
        var s = Structured(await Call(dir, McpTools.ListTargets));

        Assert.Equal(["/", "/pricing"], s["targets"]!.AsArray().Select(t => (string)t!["route"]!));
        Assert.False(s["targets"]![0]!.AsObject().ContainsKey("width")); // 沒寫的欄位不出現,不是 null
        Assert.Equal(390, (int)s["targets"]![1]!["width"]!);
        Assert.Equal("file", (string)s["designSource"]!);
        Assert.Equal("fail on: critical, serious", (string)s["gate"]!);
        Assert.EndsWith("report.json", (string)s["lastReport"]!["path"]!);
    }

    [Fact]
    public async Task Text_content_carries_the_same_json_as_structured_content()
    {
        using var dir = Project();
        var result = await Call(dir, McpTools.ListTargets);

        Assert.False((bool)result["isError"]!);
        var text = (string)result["content"]![0]!["text"]!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), result["structuredContent"]));
    }

    [Fact]
    public async Task A_missing_config_is_a_tool_error_that_says_what_to_do()
    {
        using var dir = new TempDir();
        var result = await Call(dir, McpTools.ListTargets, """{"config":"nope.json"}""");

        Assert.True((bool)result["isError"]!);
        Assert.Contains("config file not found", (string)Structured(result)["error"]!);
        Assert.Contains("parity init", (string)Structured(result)["next"]!);
    }

    // ---------- parity_get_report ----------

    [Fact]
    public async Task Get_report_lists_gating_gaps_most_severe_first_and_pages_through_them()
    {
        using var dir = Project();
        var first = Structured(await Call(dir, McpTools.GetReport, """{"limit":2}"""));
        var rest = Structured(await Call(dir, McpTools.GetReport, """{"offset":2,"limit":2}"""));

        Assert.Equal(4, (int)first["gaps"]!["total"]!); // 軟落差(fontFamily)預設不列
        Assert.Equal(["paddingTop", "fontSize"], first["gaps"]!["items"]!.AsArray().Select(i => (string)i!["prop"]!));
        Assert.Equal("Call again with offset 2 for the next gaps.", (string)first["next"]!);
        Assert.Equal(["width", "height"], rest["gaps"]!["items"]!.AsArray().Select(i => (string)i!["prop"]!));
        Assert.False(rest.ContainsKey("next"));
        var item = first["gaps"]!["items"]![0]!;
        Assert.Equal(("/", "Card", "#Card", "critical"), ((string)item["route"]!, (string)item["layer"]!, (string)item["selector"]!, (string)item["severity"]!));
        Assert.Equal("padding-top: 16px", (string)item["fix"]!);
    }

    [Theory]
    [InlineData("""{"route":"/pricing"}""", new[] { "height" })]
    [InlineData("""{"prop":"width"}""", new[] { "width" })]
    [InlineData("""{"minSeverity":"serious"}""", new[] { "paddingTop", "fontSize" })]
    [InlineData("""{"minSeverity":"SERIOUS"}""", new[] { "paddingTop", "fontSize" })]
    [InlineData("""{"includeSoft":true,"route":"/"}""", new[] { "paddingTop", "fontSize", "width", "fontFamily" })]
    public async Task Get_report_filters(string arguments, string[] props)
    {
        using var dir = Project();
        var s = Structured(await Call(dir, McpTools.GetReport, arguments));

        Assert.Equal(props, s["gaps"]!["items"]!.AsArray().Select(i => (string)i!["prop"]!));
    }

    [Fact]
    public async Task Get_report_names_the_routes_when_the_route_is_not_in_the_report()
    {
        using var dir = Project();
        var s = Structured(await Call(dir, McpTools.GetReport, """{"route":"/nope"}"""));

        Assert.Equal("The report has no target with route \"/nope\". Routes: /, /pricing.", (string)s["next"]!);
    }

    [Fact]
    public async Task Get_report_without_a_report_says_to_run_a_check_first()
    {
        using var dir = Project(withReport: false);
        var result = await Call(dir, McpTools.GetReport);

        Assert.True((bool)result["isError"]!);
        Assert.Equal("There is no report yet: call parity_check first.", (string)Structured(result)["next"]!);
    }

    [Fact]
    public async Task Get_report_says_when_the_report_is_reversed()
    {
        using var dir = Project(ReportDirection.Reverse);
        Assert.Contains("--reverse", (string)Structured(await Call(dir, McpTools.GetReport))["note"]!);
    }

    [Theory]
    [InlineData("""{"bogus":1}""", "unknown argument \"bogus\"")]
    [InlineData("""{"limit":"5"}""", "limit must be an integer")]
    [InlineData("""{"limit":2.5}""", "limit must be an integer")]
    [InlineData("""{"limit":0}""", "limit must be between 1 and 200")]
    [InlineData("""{"limit":201}""", "limit must be between 1 and 200")]
    [InlineData("""{"offset":-1}""", "offset must be between 0")]
    [InlineData("""{"includeSoft":"yes"}""", "includeSoft must be true or false")]
    [InlineData("""{"route":3}""", "route must be a string")]
    [InlineData("""{"minSeverity":"none"}""", "minSeverity must be one of")]
    public async Task Bad_arguments_are_tool_errors_the_agent_can_fix(string arguments, string message)
    {
        using var dir = Project();
        var result = await Call(dir, McpTools.GetReport, arguments);

        Assert.True((bool)result["isError"]!);
        Assert.Contains(message, (string)Structured(result)["error"]!);
        Assert.Equal("Fix the argument and call the tool again.", (string)Structured(result)["next"]!);
    }

    [Fact]
    public async Task A_whole_number_written_as_a_float_is_accepted()
    {
        using var dir = Project();
        Assert.Single(Structured(await Call(dir, McpTools.GetReport, """{"limit":1.0}"""))["gaps"]!["items"]!.AsArray());
    }

    [Fact]
    public void Prune_drops_null_members_at_every_depth()
    {
        var node = new JsonObject { ["a"] = null, ["b"] = new JsonArray(new JsonObject { ["c"] = null, ["d"] = 1 }) };
        McpTools.Prune(node);
        Assert.Equal("""{"b":[{"d":1}]}""", node.ToJsonString());
    }
}
