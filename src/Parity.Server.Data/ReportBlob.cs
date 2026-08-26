using System.IO.Compression;
using System.Text;

namespace Parity.Server.Data;

/// <summary>
/// 報告原文的入庫壓縮。規畫書 5.4 原以 ~200KB 級報告推定「數值報告很小,永久保留無妨」,
/// M2 實測改寫了前提:21 頁的 dogfooding 站一次 push 就是 10.3MB——gzip 對這種
/// 重複結構的 JSON 約 10:1,先把一個數量級省回來;完整的保留策略等真實使用量再定。
/// </summary>
public static class ReportBlob
{
    public static byte[] Compress(string json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    public static string Decompress(byte[] blob)
    {
        using var input = new GZipStream(new MemoryStream(blob), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
