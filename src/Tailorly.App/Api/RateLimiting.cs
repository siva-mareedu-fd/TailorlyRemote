using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Tailorly.Core;

namespace Tailorly.App.Api;

/// <summary>
/// Rate-limit buckets from ApiDesign §6. Limits are configuration (<c>RateLimits:{bucket}</c>); partitions are
/// the user id, falling back to the client IP. Per-email partitions for register/mail are added with those endpoints (M1).
/// </summary>
public static class RateLimiting
{
    public const string AuthLogin = "auth-login";
    public const string AuthRegister = "auth-register";
    public const string AuthMail = "auth-mail";
    public const string AiStart = "ai-start";
    public const string ManualRun = "manual-run";
    public const string ApiDefault = "api-default";
    public const string Download = "download";
    public const string Admin = "admin";

    /// <summary>Default limits: (permits, window). Overridable via configuration.</summary>
    private static readonly Dictionary<string, (int Permits, TimeSpan Window, bool ByIpOnly)> Defaults = new()
    {
        [AuthLogin] = (10, TimeSpan.FromMinutes(1), true),
        [AuthRegister] = (5, TimeSpan.FromHours(1), true),
        [AuthMail] = (5, TimeSpan.FromHours(1), true),
        [AiStart] = (30, TimeSpan.FromHours(1), false),
        [ManualRun] = (3, TimeSpan.FromDays(1), false),
        [ApiDefault] = (120, TimeSpan.FromMinutes(1), false),
        [Download] = (60, TimeSpan.FromMinutes(1), false),
        [Admin] = (60, TimeSpan.FromMinutes(1), false),
    };

    public sealed class BucketOptions
    {
        public int? PermitLimit { get; set; }
        public int? WindowSeconds { get; set; }
    }

    public static void AddTailorlyRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var limits = Defaults.ToDictionary(kv => kv.Key, kv =>
        {
            var o = config.GetSection($"RateLimits:{kv.Key}").Get<BucketOptions>();
            return (Permits: o?.PermitLimit ?? kv.Value.Permits,
                    Window: o?.WindowSeconds is { } s ? TimeSpan.FromSeconds(s) : kv.Value.Window,
                    kv.Value.ByIpOnly);
        });

        services.AddRateLimiter(o =>
        {
            foreach (var (name, (permits, window, byIpOnly)) in limits)
            {
                o.AddPolicy(name, http => RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(http, byIpOnly),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permits,
                        Window = window,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            }

            o.OnRejected = async (ctx, ct) =>
            {
                var http = ctx.HttpContext;
                var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                if (policy is not null && limits.TryGetValue(policy, out var l))
                    http.Response.Headers["RateLimit-Limit"] = l.Permits.ToString(CultureInfo.InvariantCulture);
                http.Response.Headers["RateLimit-Remaining"] = "0";
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                await Problems.WriteAsync(http, StatusCodes.Status429TooManyRequests, ErrorCodes.RateLimited,
                    "Too many requests", "Please wait a moment and try again.");
            };
        });
    }

    private static string PartitionKey(HttpContext http, bool byIpOnly)
    {
        if (!byIpOnly)
        {
            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(userId)) return "u:" + userId;
        }
        return "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
