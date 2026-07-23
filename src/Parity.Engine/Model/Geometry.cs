using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parity.Engine.Model;

/// <summary>方框:x,y 為相對各自 root 原點的座標(見規畫書 4.6)。</summary>
// JSON 欄位名見 BoxJsonConverter:寫出 width/height(自我解釋),讀時 width/height 與
// 舊的 w/h 都收——新工具吃得動 0.9.x 的舊快照/報告,升級不必重拍。C# 內部維持 .W/.H。
[JsonConverter(typeof(BoxJsonConverter))]
public readonly record struct Box(double X, double Y, double W, double H);

/// <summary>
/// Box 的 JSON 讀寫。**寫**:x/y/width/height(縮寫換成自我解釋的全名,報告是跨工具契約)。
/// **讀**:width/height 與舊的 w/h 都認,讓新工具讀得動 0.9.x 產的舊檔(不必重拍)。
/// 註:0.9.7 只認 w/h 是既成事實(已發佈、無法回頭);此處只保證「新工具吃全部」這個方向。
/// </summary>
internal sealed class BoxJsonConverter : JsonConverter<Box>
{
    public override Box Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Box 應為 JSON 物件。");

        double x = 0, y = 0, w = 0, h = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return new Box(x, y, w, h);
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;
            var name = reader.GetString();
            reader.Read();
            switch (name?.ToLowerInvariant())
            {
                case "x": x = reader.GetDouble(); break;
                case "y": y = reader.GetDouble(); break;
                case "width" or "w": w = reader.GetDouble(); break;
                case "height" or "h": h = reader.GetDouble(); break;
                default: reader.Skip(); break;
            }
        }
        throw new JsonException("Box 物件未正常結束。");
    }

    public override void Write(Utf8JsonWriter writer, Box value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteNumber("width", value.W);
        writer.WriteNumber("height", value.H);
        writer.WriteEndObject();
    }
}

/// <summary>四邊內距。</summary>
public readonly record struct Insets(double Top, double Right, double Bottom, double Left)
{
    public static readonly Insets Zero = new(0, 0, 0, 0);
}
