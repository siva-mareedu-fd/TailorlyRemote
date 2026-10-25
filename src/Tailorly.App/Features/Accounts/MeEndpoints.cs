using Hangfire;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Api;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Time;

namespace Tailorly.App.Features.Accounts;

/// <summary>Profile, consent and account deletion (ApiDesign §4.2–4.3).</summary>
public static partial class MeEndpoints
{
    public static void MapMeEndpoints(this RouteGroupBuilder api)
    {
        var me = api.MapGroup("/me").WithTags("Account");
        me.MapGet("", GetMe).WithName("GetMe").RequireAuthorization(AuthPolicies.UserAny);
        me.MapPut("", UpdateMe).WithName("UpdateMe").RequireAuthorization(AuthPolicies.User);
        me.MapPost("/consent", RecordConsent).WithName("RecordConsent").RequireAuthorization(AuthPolicies.User);

        var account = api.MapGroup("/account").WithTags("Account");
        account.MapDelete("", RequestDeletion).WithName("RequestAccountDeletion").RequireAuthorization(AuthPolicies.User)
            .RequireRateLimiting(RateLimiting.AuthMail);
        account.MapPost("/confirm-deletion", ConfirmDeletion).WithName("ConfirmAccountDeletion").AllowAnonymous();
    }

    internal static async Task<MeResponse> BuildMeAsync(AppDbContext db, UserManager<AppUser> users, AppUser user, CancellationToken ct)
    {
        var p = await db.UserProfiles.AsNoTracking().FirstAsync(x => x.UserId == user.Id, ct);
        var roles = await users.GetRolesAsync(user);
        return new MeResponse(
            user.Id,
            user.Email!,
            user.EmailConfirmed,
            p.DisplayName,
            p.TimeZone,
            p.NotificationEmail,
            p.ConsentVersion is { } v && p.ConsentedAt is { } at ? new ConsentInfo(v, at) : null,
            p.AccountStatus,
            [.. roles.Order(StringComparer.Ordinal)]);
    }

    private static async Task<AppUser> RequireUserAsync(UserManager<AppUser> users, ICurrentUser current) =>
        await users.FindByIdAsync(current.UserId.ToString())
        ?? throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthenticated, "Sign in required");

    private static async Task<Ok<MeResponse>> GetMe(AppDbContext db, UserManager<AppUser> users, ICurrentUser current,
        CancellationToken ct) =>
        TypedResults.Ok(await BuildMeAsync(db, users, await RequireUserAsync(users, current), ct));

    private static async Task<Ok<MeResponse>> UpdateMe(UpdateMeRequest body, AppDbContext db, UserManager<AppUser> users,
        ICurrentUser current, TimeProvider clock, CancellationToken ct)
    {
        var errors = new FieldErrors();
        var name = errors.Text("displayName", body.DisplayName, 100, label: "Name");
        var tz = errors.Text("timeZone", body.TimeZone, 64, label: "Time zone") is { } given ? TimeZones.Canonicalize(given) : null;
        if (tz is not null && !TimeZones.IsValid(tz)) errors.Add("timeZone", "Unknown time zone.");
        var email = errors.Email("notificationEmail", body.NotificationEmail);
        errors.ThrowIfAny();

        var user = await RequireUserAsync(users, current);
        var profile = await db.UserProfiles.FirstAsync(p => p.UserId == user.Id, ct);
        profile.DisplayName = name!;
        profile.TimeZone = tz!;
        profile.NotificationEmail = email!;
        profile.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await BuildMeAsync(db, users, user, ct));
    }

    private static async Task<NoContent> RecordConsent(ConsentRequest body, HttpContext http, AppDbContext db,
        ICurrentUser current, IConfiguration config, ConsentIpHasher ipHash, TimeProvider clock, CancellationToken ct)
    {
        if (body.Version != AuthEndpoints.CurrentConsentVersion(config))
            throw FieldErrors.Single("version", "Accept the current version of the Privacy Notice.");
        var profile = await db.UserProfiles.FirstAsync(p => p.UserId == current.UserId, ct);
        profile.ConsentVersion = body.Version;
        profile.ConsentedAt = clock.GetUtcNow();
        profile.ConsentIpHash = ipHash.Hash(http);
        profile.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Accepted<DeletionStatusResponse>> RequestDeletion(ICurrentUser current, IBackgroundJobClient jobs,
        AuditWriter audit, CancellationToken ct)
    {
        var userId = current.UserId;
        jobs.Enqueue<AccountEmailJob>(j => j.SendAsync(AccountEmailKind.ConfirmDeletion, userId, CancellationToken.None));
        await audit.WriteAsync("account.delete.requested", userId, "user", userId.ToString("D"), null, ct);
        return TypedResults.Accepted((string?)null, new DeletionStatusResponse("confirmation_sent"));
    }

    private static async Task<Accepted<DeletionStatusResponse>> ConfirmDeletion(ConfirmDeletionRequest body, HttpContext http,
        UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db, AccountDeletionTokens tokens,
        DeletionLog deletionLog, AuditWriter audit, IBackgroundJobClient jobs, TimeProvider clock, ILoggerFactory loggers,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(body.UserId.ToString());
        UserProfile? profile = null;
        if (user is not null)
            using (CurrentUser.ActAs(user.Id))
                profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id, ct);

        if (user is null || profile is null)
            throw FieldErrors.Single("token", "This link is invalid or has expired.");
        if (profile.AccountStatus == AccountStatus.PendingDeletion)
            return TypedResults.Accepted((string?)null, new DeletionStatusResponse("deletion_scheduled")); // link opened twice
        if (!tokens.Validate(body.Token, user.Id, await users.GetSecurityStampAsync(user)))
            throw FieldErrors.Single("token", "This link is invalid or has expired. Request deletion again from Account.");

        var now = clock.GetUtcNow();
        profile.AccountStatus = AccountStatus.PendingDeletion;
        profile.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await users.UpdateSecurityStampAsync(user); // other sessions stop validating

        if (http.User.Identity?.IsAuthenticated == true)
        {
            await signIn.SignOutAsync();
            http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
            CsrfProtection.IssueTokens(http);
        }

        await deletionLog.AppendAsync(user.Id, now, ct);
        await audit.WriteAsync("account.delete.confirmed", null, "user", user.Id.ToString("D"), null, ct);
        var id = user.Id;
        jobs.Enqueue<PurgeDeletedAccountsJob>(j => j.PurgeUserAsync(id, CancellationToken.None));
        LogDeletionConfirmed(loggers.CreateLogger(typeof(MeEndpoints)), id);
        return TypedResults.Accepted((string?)null, new DeletionStatusResponse("deletion_scheduled"));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Account deletion confirmed for user {UserId}; purge enqueued")]
    private static partial void LogDeletionConfirmed(ILogger logger, Guid userId);
}
