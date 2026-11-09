using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Parity.Engine.DesignSources.Fig;

/// <summary>
/// Figma 桌面版「另存本機副本」(File → Save local copy)存出來的 .fig 檔(2.5.0)。
/// 格式不是 Figma 公開的,是社群逆向出來的(參考過 figma-audit 的 fig_parser.py,MIT):
/// <list type="bullet">
/// <item>外層通常是 ZIP,設計樹在裡面的 <c>canvas.fig</c>;舊檔直接就是 canvas.fig 的內容。</item>
/// <item>canvas.fig:8 個位元組的標頭(<c>fig-kiwi</c>,FigJam 是 <c>fig-jam.</c>)、4 個位元組的版本(little-endian),
/// 接著一段段的區塊,每段是 4 個位元組的長度 + 壓縮過的內容。第一段是 Kiwi schema,第二段是資料。</item>
/// <item>壓縮是 zstd(開頭 28 B5 2F FD)或 raw deflate。</item>
/// </list>
/// 格式會隨 Figma 改版變,所以版本超出測過的範圍時明講,讀不懂時說清楚怎麼退回 API 或快照。
/// </summary>
internal static class FigFile
{
    /// <summary>實際讀過的版本範圍。舊的版本號沒有 schema 嵌入的保證;比這個新的照樣試,但讀壞時訊息會提到版本。</summary>
    internal const uint MinVersion = 15, TestedVersion = 101;

    /// <summary>防呆:壓縮內容解開後的上限。正常的大檔是幾十 MB。</summary>
    private const long MaxDecompressedBytes = 512L * 1024 * 1024;

    public static (uint Version, JsonObject Message) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var canvas = bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K' ? CanvasFromZip(bytes, path) : bytes;
        return ReadCanvas(canvas, path);
    }

    private static byte[] CanvasFromZip(byte[] bytes, string path)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.GetEntry("canvas.fig")
            ?? throw new InvalidOperationException($"{path} is a ZIP but has no canvas.fig inside — is it a .fig saved with File → Save local copy?");
        if (entry.Length > MaxDecompressedBytes) throw new InvalidOperationException($"{path}: canvas.fig is larger than {MaxDecompressedBytes >> 20} MB.");
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    internal static (uint Version, JsonObject Message) ReadCanvas(byte[] canvas, string path)
    {
        if (canvas.Length < 12 || !Encoding.ASCII.GetString(canvas, 0, 4).Equals("fig-", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{path} is not a Figma .fig file (no fig-kiwi header). Save it from the Figma desktop app with File → Save local copy.");
        var version = BitConverter.ToUInt32(canvas, 8);
        if (version < MinVersion)
            throw new InvalidOperationException($"{path} is a .fig file of format version {version}, older than Parity can read ({MinVersion}+). Open it in Figma and save a local copy again.");

        var chunks = new List<byte[]>();
        var offset = 12;
        while (offset + 4 <= canvas.Length && chunks.Count < 2)
        {
            var size = BitConverter.ToUInt32(canvas, offset);
            offset += 4;
            if (size > canvas.Length - offset) throw Broken(path, version, "a chunk runs past the end of the file");
            chunks.Add(Decompress(canvas.AsSpan(offset, (int)size).ToArray(), path, version));
            offset += (int)size;
        }
        if (chunks.Count < 2) throw Broken(path, version, "the schema or the data chunk is missing");

        try
        {
            var schema = KiwiSchema.Decode(chunks[0]);
            return (version, schema.DecodeMessage(chunks[1], "Message"));
        }
        catch (KiwiFormatException ex)
        {
            throw Broken(path, version, ex.Message);
        }
    }

    private static InvalidOperationException Broken(string path, uint version, string why) => new(
        $"{path}: could not read this .fig file ({why}; format version {version}" +
        (version > TestedVersion ? $", newer than the newest Parity was tested with, {TestedVersion}" : "") +
        "). Use the Figma API (figmaFileKey) or a snapshot instead, and please report it.");

    private static byte[] Decompress(byte[] data, string path, uint version)
    {
        try
        {
            if (data.Length >= 4 && data[0] == 0x28 && data[1] == 0xB5 && data[2] == 0x2F && data[3] == 0xFD)
            {
                using var zstd = new ZstdSharp.DecompressionStream(new MemoryStream(data));
                return ReadLimited(zstd);
            }
            using var deflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            return ReadLimited(deflate);
        }
        catch (Exception ex) when (ex is InvalidDataException or ZstdSharp.ZstdException or IOException)
        {
            throw Broken(path, version, "a chunk could not be decompressed");
        }
    }

    private static byte[] ReadLimited(Stream s)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > MaxDecompressedBytes) throw new InvalidDataException("decompressed data too large");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
