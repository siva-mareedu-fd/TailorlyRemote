using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Tailorly.App.Api;
using Tailorly.Core;

namespace Tailorly.App.Features.Accounts;

/// <summary>
/// Limits that need the request body or the user id rather than the IP (ApiDesign §6): 3 signups per email per hour,
/// 5 mails (reset) per email per hour, 3 verification resends per user per hour. Partition keys are hashed, so raw
/// addresses are not kept in memory. Limits are configuration (<c>RateLimits:{name}</c>).
/// </summary>
public sealed class AccountRateLimits : IDisposable
{
    public const string RegisterEmail = "auth-register-email";
    public const string MailEmail = "auth-mail-email";
    public const string ResendUser = "auth-resend-user";

    private readonly Dictionary<string, (PartitionedRateLimiter<string> Limiter, int Permits)> _limiters = new();

    public AccountRateLimits(IConfiguration config)
    {
        Add(config, RegisterEmail, 3, TimeSpan.FromHours(1));
        Add(config, MailEmail, 5, TimeSpan.FromHours(1));
        Add(config, ResendUser, 3, TimeSpan.FromHours(1));
    }

    private void Add(IConfiguration config, string name, int permits, TimeSpan window)
    {
        var o = config.GetSection($"RateLimits:{name}").Get<RateLimiting.BucketOptions>();
        var p = o?.PermitLimit ?? permits;
        var w = o?.WindowSeconds is { } s ? TimeSpan.FromSeconds(s) : window;
        _limiters[name] = (PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = p, Window = w, QueueLimit = 0, AutoReplenishment = true })), p);
    }

    /// <summary>Takes one permit or throws 429 <c>RATE_LIMITED</c> with <c>Retry-After</c>.</summary>
    public void Acquire(string name, string key)
    {
        var (limiter, permits) = _limiters[name];
        using var lease = limiter.AttemptAcquire(Hash(key));
        if (lease.IsAcquired) return;
        var problem = new ApiException(StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited, "Too many requests",
                "Please wait a while and try again.")
            .WithHeader("RateLimit-Limit", permits.ToString(CultureInfo.InvariantCulture))
            .WithHeader("RateLimit-Remaining", "0");
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
            problem.WithHeader("Retry-After", ((int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
        throw problem;
    }

    /// <summary>Takes one permit if available; false when the limit is reached (for optional mail that must not fail the request).</summary>
    public bool TryAcquire(string name, string key)
    {
        using var lease = _limiters[name].Limiter.AttemptAcquire(Hash(key));
        return lease.IsAcquired;
    }

    private static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim().ToLowerInvariant())));

    public void Dispose()
    {
        foreach (var (l, _) in _limiters.Values) l.Dispose();
    }
}
