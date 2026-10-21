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
