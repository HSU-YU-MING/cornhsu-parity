using System.Text;
using System.Text.Json.Nodes;

namespace Parity.Engine.DesignSources.Fig;

/// <summary>
/// Kiwi 二進位格式的讀取(2.5.0,.fig 來源用)。格式是公開的:https://github.com/evanw/kiwi(MIT)——
/// 這裡照它的二進位規則重寫,沒有抄程式碼。Figma 的 .fig 檔把自己的 schema 跟資料放在一起,所以讀的時候先解 schema、再照 schema 解資料,
/// 不必寫死 Figma 的欄位表:Figma 加了欄位,這裡照樣讀得出來。
/// </summary>
internal sealed class KiwiReader(byte[] data)
{
    private int _index;

    public bool AtEnd => _index >= data.Length;

    public byte ReadByte()
    {
        if (_index >= data.Length) throw new KiwiFormatException("unexpected end of data");
        return data[_index++];
    }

    public byte[] ReadBytes(int count)
    {
        if (count < 0 || _index + count > data.Length) throw new KiwiFormatException("unexpected end of data");
        var bytes = data.AsSpan(_index, count).ToArray();
        _index += count;
        return bytes;
    }

    public uint ReadVarUint()
    {
        uint value = 0;
        var shift = 0;
        byte b;
        do
        {
            b = ReadByte();
            value |= (uint)(b & 127) << shift;
            shift += 7;
        } while ((b & 128) != 0 && shift < 35);
        return value;
    }

    public int ReadVarInt()
    {
        var value = ReadVarUint();
        return (value & 1) != 0 ? ~(int)(value >> 1) : (int)(value >> 1);
    }

    public ulong ReadVarUint64()
    {
        ulong value = 0;
        var shift = 0;
        byte b;
        do
        {
            b = ReadByte();
            value |= (ulong)(b & 127) << shift;
            shift += 7;
        } while ((b & 128) != 0 && shift < 70);
        return value;
    }

    public long ReadVarInt64()
    {
        var value = ReadVarUint64();
        return (value & 1) != 0 ? ~(long)(value >> 1) : (long)(value >> 1);
    }

    /// <summary>Kiwi 的浮點數:0 只佔一個位元組;其他是四個位元組,指數位元移到最前面(小數值的 varint 比較短)。</summary>
    public float ReadVarFloat()
    {
        var first = ReadByte();
        if (first == 0) return 0;
        var bits = (uint)first | (uint)ReadByte() << 8 | (uint)ReadByte() << 16 | (uint)ReadByte() << 24;
        bits = (bits << 23) | (bits >> 9);
        return BitConverter.UInt32BitsToSingle(bits);
    }

    /// <summary>以 0 結尾的 UTF-8。</summary>
    public string ReadString()
    {
        var start = _index;
        while (true)
        {
            if (_index >= data.Length) throw new KiwiFormatException("unterminated string");
            if (data[_index++] == 0) break;
        }
        return Encoding.UTF8.GetString(data, start, _index - start - 1);
    }
}

internal sealed class KiwiFormatException(string message) : Exception(message);

internal enum KiwiKind { Enum = 0, Struct = 1, Message = 2 }

internal sealed record KiwiField(string Name, string Type, bool IsArray, uint Value);

internal sealed record KiwiDefinition(string Name, KiwiKind Kind, IReadOnlyList<KiwiField> Fields)
{
    public Dictionary<uint, KiwiField> ByValue { get; } = Fields.GroupBy(f => f.Value).ToDictionary(g => g.Key, g => g.First());
}

/// <summary>Kiwi schema:定義的清單。資料怎麼解,全看這一份。</summary>
internal sealed class KiwiSchema
{
    private static readonly string[] Native = ["bool", "byte", "int", "uint", "float", "string", "int64", "uint64"];

    public IReadOnlyDictionary<string, KiwiDefinition> Definitions { get; }

    private KiwiSchema(Dictionary<string, KiwiDefinition> definitions) => Definitions = definitions;

    public static KiwiSchema Decode(byte[] bytes)
    {
        var r = new KiwiReader(bytes);
        var count = r.ReadVarUint();
        if (count > 100_000) throw new KiwiFormatException($"implausible schema size ({count} definitions)");
        var raw = new List<(string Name, KiwiKind Kind, List<(string Name, int Type, bool IsArray, uint Value)> Fields)>();
        for (var i = 0; i < count; i++)
        {
            var name = r.ReadString();
            var kind = r.ReadByte();
            if (kind > 2) throw new KiwiFormatException($"unknown definition kind {kind}");
            var fieldCount = r.ReadVarUint();
            var fields = new List<(string, int, bool, uint)>();
            for (var j = 0; j < fieldCount; j++)
                fields.Add((r.ReadString(), r.ReadVarInt(), (r.ReadByte() & 1) != 0, r.ReadVarUint()));
            raw.Add((name, (KiwiKind)kind, fields));
        }

        var definitions = new Dictionary<string, KiwiDefinition>(StringComparer.Ordinal);
        foreach (var (name, kind, fields) in raw)
        {
            var resolved = fields.Select(f => new KiwiField(f.Name,
                kind == KiwiKind.Enum ? "" // enum 的欄位就是成員:沒有型別,value 是成員的數值
                    : f.Type < 0 ? (~f.Type < Native.Length ? Native[~f.Type] : throw new KiwiFormatException($"unknown native type {f.Type}"))
                    : f.Type < raw.Count ? raw[f.Type].Name : throw new KiwiFormatException($"field {name}.{f.Name} points at definition {f.Type}"),
                f.IsArray, f.Value)).ToList();
            definitions[name] = new KiwiDefinition(name, kind, resolved);
        }
        return new KiwiSchema(definitions);
    }

    /// <summary>
    /// 照 schema 解一份資料成 JSON 樹:message、struct → 物件,enum → 成員名稱,陣列 → 陣列。
    /// byte 陣列(向量路徑、圖片)只留長度,不展開成數字陣列——Parity 用不到,展開會把記憶體吃掉。
    /// </summary>
    public JsonObject DecodeMessage(byte[] bytes, string rootType)
    {
        if (!Definitions.TryGetValue(rootType, out var root)) throw new KiwiFormatException($"the schema has no {rootType} type");
        var r = new KiwiReader(bytes);
        return (JsonObject)DecodeDefinition(r, root, 0);
    }

    private const int MaxDepth = 512;

    private JsonNode DecodeDefinition(KiwiReader r, KiwiDefinition def, int depth)
    {
        if (depth > MaxDepth) throw new KiwiFormatException("data nests too deeply");
        switch (def.Kind)
        {
            case KiwiKind.Enum:
                var value = r.ReadVarUint();
                return def.ByValue.TryGetValue(value, out var member) ? JsonValue.Create(member.Name) : JsonValue.Create(value);
            case KiwiKind.Struct:
                var obj = new JsonObject();
                foreach (var f in def.Fields) obj[f.Name] = DecodeField(r, f, depth);
                return obj;
            default:
                var message = new JsonObject();
                while (true)
                {
                    var tag = r.ReadVarUint();
                    if (tag == 0) return message;
                    if (!def.ByValue.TryGetValue(tag, out var field))
                        throw new KiwiFormatException($"{def.Name} has no field {tag}");
                    message[field.Name] = DecodeField(r, field, depth);
                }
        }
    }

    private JsonNode? DecodeField(KiwiReader r, KiwiField f, int depth)
    {
        if (!f.IsArray) return DecodeValue(r, f.Type, depth + 1);
        var length = r.ReadVarUint();
        if (f.Type == "byte")
        {
            r.ReadBytes(checked((int)length));
            return new JsonObject { ["byteLength"] = length };
        }
        var array = new JsonArray();
        for (var i = 0; i < length; i++) array.Add(DecodeValue(r, f.Type, depth + 1));
        return array;
    }

    private JsonNode? DecodeValue(KiwiReader r, string type, int depth) => type switch
    {
        "bool" => JsonValue.Create(r.ReadByte() != 0),
        "byte" => JsonValue.Create(r.ReadByte()),
        "int" => JsonValue.Create(r.ReadVarInt()),
        "uint" => JsonValue.Create(r.ReadVarUint()),
        // float 轉 double 會多出尾數(0.1f → 0.10000000149):取 float 最短的十進位寫法,跟 REST 回的 0.1 才會是同一個數
        "float" => JsonValue.Create(r.ReadVarFloat() is var v && float.IsFinite(v)
            ? double.Parse(v.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture)
            : 0d),
        "string" => JsonValue.Create(r.ReadString()),
        "int64" => JsonValue.Create(r.ReadVarInt64()),
        "uint64" => JsonValue.Create(r.ReadVarUint64()),
        _ => Definitions.TryGetValue(type, out var def) ? DecodeDefinition(r, def, depth) : throw new KiwiFormatException($"unknown type {type}"),
    };
}
