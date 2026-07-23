using System.Text.Json.Serialization;

namespace Parity.Engine.Model;

/// <summary>方框:x,y 為相對各自 root 原點的座標(見規畫書 4.6)。</summary>
// W/H 在 JSON 序列化成 width/height:報告是跨工具契約,自我解釋的欄位名勝過縮寫
// (C# 內部維持 .W/.H 不變)。設計/快照 JSON 亦同,舊檔(w/h)需重拍。
public readonly record struct Box(
    double X,
    double Y,
    [property: JsonPropertyName("width")] double W,
    [property: JsonPropertyName("height")] double H);

/// <summary>四邊內距。</summary>
public readonly record struct Insets(double Top, double Right, double Bottom, double Left)
{
    public static readonly Insets Zero = new(0, 0, 0, 0);
}
