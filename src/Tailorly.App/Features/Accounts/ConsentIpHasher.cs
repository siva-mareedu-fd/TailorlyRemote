using System.Security.Cryptography;
using System.Text;

namespace Tailorly.App.Features.Accounts;

/// <summary>
/// <c>consent_ip_hash</c> (PRD F1): HMAC-SHA256 of the client IP with a secret key from the environment
/// (<c>Security__IpHashKey</c>), so the stored value cannot be reversed by hashing all IPv4 addresses.
/// </summary>
public sealed partial class ConsentIpHasher
{
    private readonly byte[] _key;

    public ConsentIpHasher(IConfiguration config, ILogger<ConsentIpHasher> logger)
    {
        var key = config["Security:IpHashKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            LogNoKey(logger);
            key = "tailorly-consent-ip-unkeyed";
        }
        _key = Encoding.UTF8.GetBytes(key);
    }

    public string? Hash(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString();
        if (ip is null) return null;
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(ip)));
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Security:IpHashKey is not set; consent IP hashes use a fixed key. Set Security__IpHashKey in production")]
    private static partial void LogNoKey(ILogger logger);
}
