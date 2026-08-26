using System.Security.Cryptography;

namespace Parity.Server.Data;

/// <summary>
/// 專案 API token:建立時印一次、只存 SHA-256——伺服器被翻庫也拿不回 token 本體。
/// 驗證是「hash 後查索引」,天然定時安全(不比對明文)。
/// </summary>
public static class ProjectToken
{
    /// <summary>產生新 token(32 bytes 隨機,base64url,無填充)。呼叫端負責「只顯示這一次」。</summary>
    public static string Generate()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>token → 存庫用的 SHA-256 hex。</summary>
    public static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
