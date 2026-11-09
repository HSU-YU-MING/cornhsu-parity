namespace Parity.Engine;

// report.json 的型別(1.0 凍結面 4)。住在 Parity.Report 這個不含擷取端的組件,
// 命名空間沿用 Parity.Engine——見 Parity.Report.csproj 的說明。

/// <summary>落差嚴重度。</summary>
public enum Severity { None = 0, Minor = 1, Medium = 2, Serious = 3, Critical = 4 }

public enum DiffStatus { Mismatch, Missing }

/// <summary>單一屬性的落差:「內距 8px,設計 12px」精確到數字(規畫書 1)。</summary>
public sealed record PropDiff(
    string Prop,
    string Expected,
    string Actual,
    string? Unit,
    double? Delta,
    double Tolerance,
    Severity Severity,
    DiffStatus Status = DiffStatus.Mismatch,
    bool Soft = false);

/// <summary>一個配對成功的設計節點的比對結果。兩個框給疊圖視圖用(M3)。</summary>
public sealed record NodeResult(
    string DesignLayer,
    string DesignId,
    string Selector,
    string MatchedBy,
    Severity Severity,
    IReadOnlyList<PropDiff> Diffs,
    Model.Box DesignBox = default,
    Model.Box RenderedBox = default);

/// <summary>只在設計端找得到、配不到實作的節點——誠實列出(規畫書 4.7)。</summary>
public sealed record UnmatchedNode(string DesignLayer, string DesignId, string Reason, Model.Box DesignBox = default);

public sealed record ReportSummary(
    int DesignNodes,
    int Matched,
    int Unmatched,
    int NodesWithDiffs,
    int Critical,
    int Serious,
    int Medium,
    int Minor,
    Severity MaxSeverity);

/// <summary>
/// 引擎輸出:可序列化成 JSON / 餵給 UI / 決定 exit code(規畫書 4.1)。
/// <see cref="Extras"/> 是 2.x 的加法(compare.extras),放在最後、有預設值:舊報告沒有這個 key,讀起來是 null。
/// <see cref="FontsNotLoaded"/>(2.5.0)同樣的加法:頁面要用、卻沒載到的 web font(「字型名 粗細 樣式」);null = 都載到了或擷取端沒給。
/// </summary>
public sealed record FidelityReport(
    string Route,
    string Url,
    string DesignReference,
    IReadOnlyList<NodeResult> Nodes,
    IReadOnlyList<UnmatchedNode> Unmatched,
    ReportSummary Summary,
    ExtraElements? Extras = null,
    IReadOnlyList<string>? FontsNotLoaded = null);

/// <summary>
/// 實作端多出來的元件(<c>compare.extras: "list"</c>)。**null = 沒開**(預設、<c>check --reverse</c>、
/// 這個欄位出現之前的舊報告);開了但沒找到是 <c>{ total: 0, items: [] }</c>——讀的人分得出「沒開」和「沒有」。
/// <see cref="Total"/> 是截斷前的總數,<see cref="Items"/> 依面積由大到小、有上限。
/// 只是清單:不進 gate、分數、summary、baseline。匿名統計只送 <see cref="Total"/>,不送 <see cref="Items"/>。
/// </summary>
public sealed record ExtraElements(int Total, IReadOnlyList<ExtraElement> Items);

/// <summary>
/// 多出來的一個元件:只有 selector、標籤名與框(座標與 <c>renderedBox</c> 同一套,相對 body 原點)。
/// **刻意不帶元素文字**:report.json 會被推到伺服器,不多帶一種新的內容。
/// </summary>
public sealed record ExtraElement(string Selector, string Tag, Model.Box Box);

/// <summary>
/// report.json 的頂層信封(0.10.0 起;1.0 凍結面 4)。裸陣列無從辨識版本;包一層
/// schemaVersion,報告格式演進時消費端(parity report / serve / **Parity.Server**)有據可判。
/// 原住在 Parity.Cli;網頁外殼動工(2026-08-26)時移入引擎,2026-10-22 再搬進 Parity.Report
/// (伺服器不准引用含擷取端的引擎)——報告契約的每一個消費者都該對同一個型別,而不是各抄一份。
/// 兩次搬家 wire 格式都逐位元不變。
/// </summary>
public sealed record ReportDocument(
    int SchemaVersion,
    IReadOnlyList<FidelityReport> Reports,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(ReportDirectionConverter))]
    ReportDirection Direction = ReportDirection.Forward)
{
    public const int CurrentSchemaVersion = 1;

    public static ReportDocument Of(IReadOnlyList<FidelityReport> reports, ReportDirection direction = ReportDirection.Forward)
        => new(CurrentSchemaVersion, reports, direction);
}

/// <summary>
/// 報告的方向(2.0.0 起寫進信封,一律寫出):<c>forward</c> = 期望是設計稿、實際是實作(一般的 check);
/// <c>reverse</c> = <c>check --reverse</c>,期望/實際對調、不把關。封閉集。沒有這個欄位的舊檔讀成 forward。
/// 讀端據此分辨:<c>parity report</c> 不對 reverse 報告做 gate,<c>parity push</c> 與 Parity.Server 不收它。
/// </summary>
public enum ReportDirection { Forward, Reverse }

/// <summary>
/// direction 的讀寫(2.0.0)。只收**小寫字串** `"forward"`/`"reverse"`——Parity 一向寫小寫,大小寫不同的值
/// 不是任何一版 Parity 寫出來的,不放寬;數字、`null`、其他字串一律拒收(<see cref="ReportContractException"/>)。
/// 欄位缺少才讀成 forward(record 的預設值,不經過這裡)。掛在屬性上:選項裡的字串 enum 轉換器會收數字、
/// 不分大小寫,而且屬性上的轉換器優先於選項。CLI(report、push)與 Parity.Server 都經過這一個型別。
/// </summary>
internal sealed class ReportDirectionConverter : System.Text.Json.Serialization.JsonConverter<ReportDirection>
{
    public override bool HandleNull => true;

    public override ReportDirection Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.String)
        {
            switch (reader.GetString())
            {
                case "forward": return ReportDirection.Forward;
                case "reverse": return ReportDirection.Reverse;
            }
        }
        var got = reader.TokenType switch
        {
            System.Text.Json.JsonTokenType.String => System.Text.Json.JsonSerializer.Serialize(reader.GetString()),
            System.Text.Json.JsonTokenType.Null => "null",
            System.Text.Json.JsonTokenType.True => "true",
            System.Text.Json.JsonTokenType.False => "false",
            System.Text.Json.JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
            _ => reader.TokenType.ToString().ToLowerInvariant(),
        };
        throw new ReportContractException($"\"direction\" must be \"forward\" or \"reverse\" (got {got}).");
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, ReportDirection value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            ReportDirection.Forward => "forward",
            ReportDirection.Reverse => "reverse",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "unknown report direction"),
        });
}

/// <summary>report.json 違反契約(例如 direction 不是封閉集裡的值)。是 JsonException,讀端照一般解析錯誤處理。</summary>
public sealed class ReportContractException(string message) : System.Text.Json.JsonException(message);
