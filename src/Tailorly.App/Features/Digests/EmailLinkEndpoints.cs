using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Api;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.App.Features.Digests;

/// <summary>
/// Signed email links that work without signing in (ApiDesign §4.10): one-click digest unsubscribe (RFC 8058) and the
/// notification-email confirmation (proposal 18). The token is the only credential; it names the user and is bound to
/// one purpose, so these endpoints can act for that user and nothing else.
/// </summary>
public static partial class EmailLinkEndpoints
{
    public static void MapEmailLinkEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/email").WithTags("Email links").AllowAnonymous();
        g.MapGet("/unsubscribe", UnsubscribePage).WithName("OpenUnsubscribeLink").Produces(StatusCodes.Status302Found);
        // One-click POST from mail clients carries "List-Unsubscribe=One-Click" as a form body and no CSRF header.
        g.MapPost("/unsubscribe", Unsubscribe).WithName("Unsubscribe").WithMetadata(new SkipCsrfAttribute());

        api.MapPost("/me/notification-email/verify", VerifyNotificationEmail).WithName("VerifyNotificationEmail")
            .WithTags("Account").AllowAnonymous();
    }

    /// <summary>Never changes state (mail scanners open links): sends the browser to the SPA confirmation page.</summary>
    private static RedirectHttpResult UnsubscribePage(string? token) =>
        TypedResults.Redirect("/unsubscribe?token=" + Uri.EscapeDataString(token ?? ""));

    /// <summary>Turns the daily digest off (<c>schedule_enabled = false</c>, DatabaseDesign DB11). Idempotent.</summary>
    private static async Task<Ok<UnsubscribeResult>> Unsubscribe(string? token, UnsubscribeTokens tokens, AppDbContext db,
        TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var userId = tokens.Read(token) ?? throw FieldErrors.Single("token", "This link is invalid. Turn off the digest in Preferences instead.");
        using var _ = CurrentUser.ActAs(userId);
        if (!await db.UserProfiles.AnyAsync(ct))
            throw FieldErrors.Single("token", "This link is invalid. Turn off the digest in Preferences instead.");

        var prefs = await db.UserPreferences.FirstOrDefaultAsync(ct);
        if (prefs is null)
            db.UserPreferences.Add(prefs = new UserPreferences { UserId = userId });
        if (prefs.ScheduleEnabled || db.Entry(prefs).State == EntityState.Added)
        {
            prefs.ScheduleEnabled = false;
            prefs.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        LogUnsubscribed(loggers.CreateLogger(typeof(EmailLinkEndpoints)), userId);
        return TypedResults.Ok(new UnsubscribeResult(true));
    }

    /// <summary>Confirms the notification email the link was sent to, if it is still the user's notification email.</summary>
    private static async Task<NoContent> VerifyNotificationEmail(VerifyNotificationEmailRequest body, NotificationEmailTokens tokens,
        AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var invalid = FieldErrors.Single("token", "This link is invalid or has expired. Save the address again in Account to get a new one.");
        if (tokens.Read(body.Token) is not { } link) throw invalid;
        var (userId, email) = link;
        using var _ = CurrentUser.ActAs(userId);
        var profile = await db.UserProfiles.FirstOrDefaultAsync(ct);
        if (profile is null || !string.Equals(profile.NotificationEmail.Trim(), email, StringComparison.OrdinalIgnoreCase)) throw invalid;
        if (profile.NotificationEmailVerifiedAt is null)
        {
            profile.NotificationEmailVerifiedAt = clock.GetUtcNow();
            profile.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.NoContent();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Daily digest turned off by email link for user {UserId}")]
    private static partial void LogUnsubscribed(ILogger logger, Guid userId);
}
