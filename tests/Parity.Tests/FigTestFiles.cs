using System.IO.Compression;
using System.Text;

namespace Parity.Tests;

/// <summary>
/// 測試用的 Kiwi 寫入端與 .fig 檔產生器:照 Kiwi 的二進位規則(github.com/evanw/kiwi)寫,
/// schema 用 Figma 欄位名的一個子集。讀取端(FigFile、KiwiSchema)不認得寫死的欄位表,所以這裡能驗的是「照規則解」;
/// 欄位名跟真的 Figma 檔一不一致,要拿真檔校準。
/// </summary>
internal sealed class KiwiWriter
{
    private readonly MemoryStream _out = new();

    public byte[] ToArray() => _out.ToArray();

    public KiwiWriter Byte(byte b) { _out.WriteByte(b); return this; }

    public KiwiWriter Bool(bool b) => Byte(b ? (byte)1 : (byte)0);

    public KiwiWriter VarUint(uint v)
    {
        do
        {
            var b = (byte)(v & 127);
            v >>= 7;
            if (v != 0) b |= 128;
            _out.WriteByte(b);
        } while (v != 0);
        return this;
    }

    public KiwiWriter VarInt(int v) => VarUint((uint)((v << 1) ^ (v >> 31)));

    public KiwiWriter Float(float f)
    {
        var bits = BitConverter.SingleToUInt32Bits(f);
        bits = (bits >> 23) | (bits << 9);
        if ((bits & 255) == 0) return Byte(0); // 0 與非正規數:一個位元組
        _out.WriteByte((byte)bits);
        _out.WriteByte((byte)(bits >> 8));
        _out.WriteByte((byte)(bits >> 16));
        _out.WriteByte((byte)(bits >> 24));
        return this;
    }

    public KiwiWriter String(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        _out.Write(bytes);
        _out.WriteByte(0);
        return this;
    }

    public KiwiWriter Raw(byte[] bytes) { _out.Write(bytes); return this; }
}

/// <summary>
/// 一份小小的 Figma 風格 schema 與資料。欄位名照 figma-audit 的 fig_parser.py 讀的那些(transform、size、fillPaints、stack*、
/// textData、symbolData……),數值用 Figma 的 enum 名稱。
/// </summary>
internal static class FigTestFiles
{
    // 型別編號:native 是 ~index(bool -1、byte -2、int -3、uint -4、float -5、string -6);定義是它在清單裡的位置
    private const int TBool = -1, TByte = -2, TUint = -4, TFloat = -5, TString = -6;

    private static readonly string[] Defs =
    [
        "NodeType", "PaintType", "StackMode", "NumberUnits", "GUID", "Vector", "Matrix", "Color",
        "ParentIndex", "Paint", "FontName", "Number", "TextData", "GUIDPath", "SymbolData", "NodeChange", "Message",
        "StyleId", "StackJustify",
    ];

    private static int D(string name) => Array.IndexOf(Defs, name);

    internal static readonly string[] NodeTypes = ["NONE", "DOCUMENT", "CANVAS", "GROUP", "FRAME", "TEXT", "RECTANGLE", "SYMBOL", "INSTANCE"];
    internal static readonly string[] PaintTypes = ["SOLID", "GRADIENT_LINEAR"];
    internal static readonly string[] StackModes = ["NONE", "HORIZONTAL", "VERTICAL"];
    internal static readonly string[] Units = ["RAW", "PIXELS", "PERCENT"];
    internal static readonly string[] Justify = ["MIN", "CENTER", "MAX", "SPACE_EVENLY"];

    /// <summary>NodeChange 的欄位:(名字, 型別, 是陣列, 編號)。</summary>
    internal static readonly (string Name, int Type, bool Array, uint Value)[] NodeFields =
    [
        ("guid", D("GUID"), false, 1), ("parentIndex", D("ParentIndex"), false, 2), ("type", D("NodeType"), false, 3),
        ("name", TString, false, 4), ("size", D("Vector"), false, 5), ("transform", D("Matrix"), false, 6),
        ("fillPaints", D("Paint"), true, 7), ("fontSize", TFloat, false, 8), ("fontName", D("FontName"), false, 9),
        ("lineHeight", D("Number"), false, 10), ("textData", D("TextData"), false, 11), ("stackMode", D("StackMode"), false, 12),
        ("stackSpacing", TFloat, false, 13), ("stackHorizontalPadding", TFloat, false, 14), ("stackVerticalPadding", TFloat, false, 15),
        ("symbolData", D("SymbolData"), false, 16), ("guidPath", D("GUIDPath"), false, 17), ("visible", TBool, false, 18),
        ("cornerRadius", TFloat, false, 19), ("vectorData", TByte, true, 20), ("stackPaddingRight", TFloat, false, 21),
        ("stackPaddingBottom", TFloat, false, 22), ("letterSpacing", D("Number"), false, 23),
        ("styleIdForFill", D("StyleId"), false, 24), ("overriddenSymbolID", D("GUID"), false, 25),
        ("stackPrimaryAlignItems", D("StackJustify"), false, 26), ("styleType", TString, false, 27),
    ];

    public static byte[] Schema()
    {
        var w = new KiwiWriter().VarUint((uint)Defs.Length);
        void Enum(string name, string[] members)
        {
            w.String(name).Byte(0).VarUint((uint)members.Length);
            for (var i = 0; i < members.Length; i++) w.String(members[i]).VarInt(0).Byte(0).VarUint((uint)i);
        }
        void Def(string name, byte kind, params (string Name, int Type, bool Array, uint Value)[] fields)
        {
            w.String(name).Byte(kind).VarUint((uint)fields.Length);
            foreach (var f in fields) w.String(f.Name).VarInt(f.Type).Byte(f.Array ? (byte)1 : (byte)0).VarUint(f.Value);
        }
        Enum("NodeType", NodeTypes);
        Enum("PaintType", PaintTypes);
        Enum("StackMode", StackModes);
        Enum("NumberUnits", Units);
        Def("GUID", 1, ("sessionID", TUint, false, 1), ("localID", TUint, false, 2));
        Def("Vector", 1, ("x", TFloat, false, 1), ("y", TFloat, false, 2));
        Def("Matrix", 1, ("m00", TFloat, false, 1), ("m01", TFloat, false, 2), ("m02", TFloat, false, 3),
            ("m10", TFloat, false, 4), ("m11", TFloat, false, 5), ("m12", TFloat, false, 6));
        Def("Color", 1, ("r", TFloat, false, 1), ("g", TFloat, false, 2), ("b", TFloat, false, 3), ("a", TFloat, false, 4));
        Def("ParentIndex", 2, ("guid", D("GUID"), false, 1), ("position", TString, false, 2));
        Def("Paint", 2, ("type", D("PaintType"), false, 1), ("color", D("Color"), false, 2), ("opacity", TFloat, false, 3), ("visible", TBool, false, 4));
        Def("FontName", 2, ("family", TString, false, 1), ("style", TString, false, 2));
        Def("Number", 2, ("value", TFloat, false, 1), ("units", D("NumberUnits"), false, 2));
        Def("TextData", 2, ("characters", TString, false, 1));
        Def("GUIDPath", 2, ("guids", D("GUID"), true, 1));
        Def("SymbolData", 2, ("symbolID", D("GUID"), false, 1), ("symbolOverrides", D("NodeChange"), true, 2));
        Def("NodeChange", 2, NodeFields);
        Def("Message", 2, ("nodeChanges", D("NodeChange"), true, 1));
        Def("StyleId", 2, ("guid", D("GUID"), false, 1));
        Enum("StackJustify", Justify);
        return w.ToArray();
    }

    /// <summary>寫一個 NodeChange 的欄位(值由呼叫端用 writer 寫)。</summary>
    internal static KiwiWriter Field(this KiwiWriter w, string name) => w.VarUint(NodeFields.Single(f => f.Name == name).Value);

    internal static KiwiWriter Guid(this KiwiWriter w, uint session, uint local) => w.VarUint(session).VarUint(local);

    internal static KiwiWriter Matrix(this KiwiWriter w, float x, float y) => w.Float(1).Float(0).Float(x).Float(0).Float(1).Float(y);

    internal static KiwiWriter Color(this KiwiWriter w, float r, float g, float b, float a = 1) => w.Float(r).Float(g).Float(b).Float(a);

    /// <summary>
    /// 測試用的設計:一頁(0:1)、一個 frame「Home」(1:2,在 (100, 50),400×300,auto-layout 垂直、padding 24/16、gap 12)。
    /// 裡面依序:文字「Title」(1:3,Inter Semi Bold 24px、行高 32px)、隱藏的矩形(1:4)、藍色矩形(1:5,圓角 8)、
    /// 卡片元件的實例(1:20,在 (16, 200)),元件(1:10,白底、水平 space-between)在另一頁:巢狀 frame「Body」(1:12)裡一段文字
    /// (1:11,「Default」,實例覆寫成「Buy」——路徑只有一段,中間隔著 frame),以及巢狀實例 Icon(1:13,實例把它換成 Tag 元件)。
    /// 另外兩個引用填色樣式的矩形:Styled(1:6,樣式是紅色)、Unfilled(1:7,樣式沒有任何填色);兩個自己存的都是藍色(舊快取)。
    /// 子節點故意不照順序寫:排序要靠 parentIndex.position。
    /// </summary>
    public static byte[] Message()
    {
        var w = new KiwiWriter().VarUint(1); // Message.nodeChanges
        var nodes = new List<Action<KiwiWriter>>
        {
            n => n.Field("guid").Guid(0, 0).Field("type").VarUint(1).Field("name").String("Document"),
            n => n.Field("guid").Guid(0, 1).Field("parentIndex").Field2Parent(0, 0, "!").Field("type").VarUint(2).Field("name").String("Page 1"),
            n => n.Field("guid").Guid(0, 9).Field("parentIndex").Field2Parent(0, 0, "#").Field("type").VarUint(2).Field("name").String("Components"),
            n => n.Field("guid").Guid(1, 2).Field("parentIndex").Field2Parent(0, 1, "!").Field("type").VarUint(4).Field("name").String("Home")
                .Field("size").Float(400).Float(300).Field("transform").Matrix(100, 50)
                .Field("fillPaints").VarUint(1).VarUint(1).VarUint(0).VarUint(2).Color(1, 1, 1).VarUint(0)
                .Field("stackMode").VarUint(2).Field("stackSpacing").Float(12).Field("stackHorizontalPadding").Float(16)
                .Field("stackVerticalPadding").Float(24).Field("stackPaddingRight").Float(16).Field("stackPaddingBottom").Float(24),
            // 藍色矩形(position "c")先寫,文字(position "a")後寫
            n => n.Field("guid").Guid(1, 5).Field("parentIndex").Field2Parent(1, 2, "c").Field("type").VarUint(6).Field("name").String("Button")
                .Field("size").Float(120).Float(40).Field("transform").Matrix(16, 80).Field("cornerRadius").Float(8)
                .Field("fillPaints").VarUint(1).VarUint(1).VarUint(0).VarUint(2).Color(0.1450980f, 0.3882353f, 0.9215686f).VarUint(0)
                .Field("vectorData").VarUint(3).Raw([1, 2, 3]),
            n => n.Field("guid").Guid(1, 3).Field("parentIndex").Field2Parent(1, 2, "a").Field("type").VarUint(5).Field("name").String("Title")
                .Field("size").Float(200).Float(32).Field("transform").Matrix(16, 24)
                .Field("fontSize").Float(24).Field("fontName").VarUint(1).String("Inter").VarUint(2).String("Semi Bold").VarUint(0)
                .Field("lineHeight").VarUint(1).Float(32).VarUint(2).VarUint(1).VarUint(0)
                .Field("letterSpacing").VarUint(1).Float(-2).VarUint(2).VarUint(2).VarUint(0)
                .Field("textData").VarUint(1).String("Title").VarUint(0),
            n => n.Field("guid").Guid(1, 4).Field("parentIndex").Field2Parent(1, 2, "b").Field("type").VarUint(6).Field("name").String("Hidden")
                .Field("size").Float(10).Float(10).Field("visible").Bool(false),
            n => n.Field("guid").Guid(1, 10).Field("parentIndex").Field2Parent(0, 9, "!").Field("type").VarUint(7).Field("name").String("Card")
                .Field("size").Float(160).Float(60).Field("transform").Matrix(0, 0)
                .Field("fillPaints").VarUint(1).VarUint(1).VarUint(0).VarUint(2).Color(1, 1, 1).VarUint(0)
                .Field("stackMode").VarUint(1).Field("stackPrimaryAlignItems").VarUint(3),
            n => n.Field("guid").Guid(1, 12).Field("parentIndex").Field2Parent(1, 10, "!").Field("type").VarUint(4).Field("name").String("Body")
                .Field("size").Float(100).Float(20).Field("transform").Matrix(8, 10),
            n => n.Field("guid").Guid(1, 13).Field("parentIndex").Field2Parent(1, 10, "#").Field("type").VarUint(8).Field("name").String("Icon")
                .Field("size").Float(20).Float(20).Field("transform").Matrix(120, 10).Field("symbolData").VarUint(1).Guid(1, 40).VarUint(0),
            n => n.Field("guid").Guid(1, 40).Field("parentIndex").Field2Parent(0, 9, "#").Field("type").VarUint(7).Field("name").String("Icon")
                .Field("size").Float(20).Float(20),
            n => n.Field("guid").Guid(1, 41).Field("parentIndex").Field2Parent(1, 40, "!").Field("type").VarUint(5).Field("name").String("Glyph")
                .Field("size").Float(20).Float(20).Field("fontSize").Float(12).Field("textData").VarUint(1).String("★").VarUint(0),
            n => n.Field("guid").Guid(1, 30).Field("parentIndex").Field2Parent(0, 9, "$").Field("type").VarUint(7).Field("name").String("Tag")
                .Field("size").Float(20).Float(20),
            n => n.Field("guid").Guid(1, 31).Field("parentIndex").Field2Parent(1, 30, "!").Field("type").VarUint(5).Field("name").String("Word")
                .Field("size").Float(20).Float(20).Field("fontSize").Float(12).Field("textData").VarUint(1).String("Tag").VarUint(0),
            // 樣式節點:一個紅色、一個沒有任何填色
            n => n.Field("guid").Guid(0, 20).Field("parentIndex").Field2Parent(0, 9, "%").Field("type").VarUint(6).Field("name").String("Brand/Red")
                .Field("styleType").String("FILL").Field("fillPaints").VarUint(1).VarUint(1).VarUint(0).VarUint(2).Color(1, 0, 0).VarUint(0),
            n => n.Field("guid").Guid(0, 21).Field("parentIndex").Field2Parent(0, 9, "&").Field("type").VarUint(6).Field("name").String("Empty")
                .Field("styleType").String("FILL"),
            // Home 裡兩個引用樣式的矩形:自己存的藍色是舊快取
            n => n.Field("guid").Guid(1, 6).Field("parentIndex").Field2Parent(1, 2, "e").Field("type").VarUint(6).Field("name").String("Styled")
                .Field("size").Float(50).Float(50).Field("transform").Matrix(200, 80)
                .Field("fillPaints").VarUint(1).VarUint(1).VarUint(0).VarUint(2).Color(0, 0, 1).VarUint(0)
                .Field("styleIdForFill").VarUint(1).Guid(0, 20).VarUint(0),
            n => n.Field("guid").Guid(1, 7).Field("parentIndex").Field2Parent(1, 2, "f").Field("type").VarUint(6).Field("name").String("Unfilled")
                .Field("size").Float(50).Float(50).Field("transform").Matrix(260, 80)
                .Field("fillPaints").VarUint(1).VarUint(1).VarUint(0).VarUint(2).Color(0, 0, 1).VarUint(0)
                .Field("styleIdForFill").VarUint(1).Guid(0, 21).VarUint(0),
            n => n.Field("guid").Guid(1, 11).Field("parentIndex").Field2Parent(1, 12, "!").Field("type").VarUint(5).Field("name").String("Label")
                .Field("size").Float(100).Float(20).Field("transform").Matrix(0, 0).Field("fontSize").Float(16)
                .Field("fontName").VarUint(1).String("Inter").VarUint(2).String("Regular").VarUint(0)
                .Field("textData").VarUint(1).String("Default").VarUint(0),
            n => n.Field("guid").Guid(1, 20).Field("parentIndex").Field2Parent(1, 2, "d").Field("type").VarUint(8).Field("name").String("Card")
                .Field("size").Float(160).Float(60).Field("transform").Matrix(16, 200)
                .Field("symbolData").VarUint(1).Guid(1, 10)
                    .VarUint(2).VarUint(2) // symbolOverrides:兩筆
                        .Field("guidPath").VarUint(1).VarUint(1).Guid(1, 11).VarUint(0)
                        .Field("textData").VarUint(1).String("Buy").VarUint(0)
                        .VarUint(0)
                        .Field("guidPath").VarUint(1).VarUint(1).Guid(1, 13).VarUint(0)
                        .Field("overriddenSymbolID").Guid(1, 30)
                        .VarUint(0)
                    .VarUint(0),
        };
        w.VarUint((uint)nodes.Count);
        foreach (var write in nodes)
        {
            write(w);
            w.VarUint(0); // NodeChange 結束
        }
        return w.VarUint(0).ToArray(); // Message 結束
    }

    private static KiwiWriter Field2Parent(this KiwiWriter w, uint session, uint local, string position)
        => w.VarUint(1).Guid(session, local).VarUint(2).String(position).VarUint(0);

    /// <summary>canvas.fig 的位元組:標頭、版本、壓縮過的 schema 與資料。</summary>
    public static byte[] Canvas(uint version = 48, bool zstd = true, byte[]? schema = null, byte[]? message = null, string header = "fig-kiwi")
    {
        var w = new KiwiWriter().Raw(Encoding.ASCII.GetBytes(header)).Raw(BitConverter.GetBytes(version));
        foreach (var chunk in new[] { Deflate(schema ?? Schema()), zstd ? Zstd(message ?? Message()) : Deflate(message ?? Message()) })
            w.Raw(BitConverter.GetBytes((uint)chunk.Length)).Raw(chunk);
        return w.ToArray();
    }

    /// <summary>「另存本機副本」的 ZIP 外殼,裡面一個 canvas.fig(另外放一個 meta.json,讀取端要忽略它)。</summary>
    public static byte[] Zip(byte[] canvas)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("meta.json").Open()) s.Write("{\"file_name\":\"test\"}"u8);
            using (var s = zip.CreateEntry("canvas.fig").Open()) s.Write(canvas);
        }
        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data);
        return ms.ToArray();
    }

    private static byte[] Zstd(byte[] data)
    {
        using var c = new ZstdSharp.Compressor();
        return c.Wrap(data).ToArray();
    }
}
