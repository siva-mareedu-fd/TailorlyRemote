using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tailorly.App.Ai;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Features.Jobs;
using Tailorly.App.Infrastructure;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Budgets;
using Tailorly.Core.Matching;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Digests;

/// <summary>
/// <c>SendEmail</c> for a scheduled digest (SDD §11.5): a Hangfire job retried 3 times (1/5/30 min). Idempotent through
/// <c>digests.emailed_at</c>, checked before and set after sending (at-least-once, ADR-010). It sends nothing when the
/// user turned the digest off meanwhile, the <c>digest.email_enabled</c> flag is off, or there is nothing to send and
/// <c>digest_when_empty</c> is off. Unclear-eligibility jobs are left out unless the user opted in (PRD §4 F3). The
/// recipient is the notification email once it is confirmed, otherwise the verified sign-in email (proposal 18).
/// Logs ids and counts only, never the address or the content.
/// </summary>
public sealed partial class DigestEmailJob(
    AppDbContext db,
    UserManager<AppUser> users,
    ScoredJobs scoredJobs,
    BudgetService budgets,
    IEmailSender sender,
    AppLinks links,
    UnsubscribeTokens unsubscribe,
    IOptions<DigestOptions> options,
    TimeProvider clock,
    ILogger<DigestEmailJob> logger)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 300, 1800])]
    public async Task SendAsync(Guid digestId, Guid userId, CancellationToken ct)
    {
        using var _ = CurrentUser.ActAs(userId);
        var digest = await db.Digests.FirstOrDefaultAsync(d => d.Id == digestId, ct);
        if (digest is not { Kind: DigestKinds.Scheduled, Status: WorkStatus.Ready, EmailedAt: null }) return;
        if (!await FeatureFlags.IsOnAsync(db, FeatureFlag.DigestEmailEnabled, ct)) return;

        var prefs = await MatchContext.PreferencesAsync(db, userId, ct);
        var profile = await db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(ct);
        var user = await users.FindByIdAsync(userId.ToString());
        if (!prefs.ScheduleEnabled || profile is null || profile.AccountStatus != AccountStatus.Active
            || user?.Email is null || !user.EmailConfirmed)
        {
            LogSkipped(logger, digestId, "not_deliverable");
            return;
        }

        var items = await db.DigestItems.AsNoTracking().Where(i => i.DigestId == digestId)
            .Select(i => new { i.ScoreId, i.Rank }).ToListAsync(ct);
        var rank = items.ToDictionary(i => i.ScoreId, i => i.Rank);
        var ids = rank.Keys.ToList();
        var rows = await scoredJobs.Query(db.UserJobScores.Where(s => ids.Contains(s.Id))).ToListAsync(ct);
        var views = (await scoredJobs.EvaluateAsync(rows, MatchContext.ToMatch(prefs), ct))
            .Where(v => v.Open && EligibilityResolver.Visible(v.Eligibility, inEmail: true, prefs.ShowUnclearEligibility))
            .OrderBy(v => rank[v.Row.ScoreId])
            .ToList();
        if (views.Count == 0 && !prefs.DigestWhenEmpty)
        {
            LogSkipped(logger, digestId, "empty");
            return;
        }

        var listed = await scoredJobs.ToSummariesAsync(views.Take(Math.Max(1, options.Value.EmailMaxItems)).ToList(), ct);
        string? reason = null;
        string? resetsOn = null;
        if (digest.Degraded)
        {
            var discovery = await budgets.GetAsync(userId, BudgetBuckets.Discovery, ct);
            reason = discovery.IsExhausted ? "budget" : "ai";
            resetsOn = BudgetPolicy.FormatResetsOn(budgets.ResetsOn);
        }

        var message = DigestEmailTemplate.Build(new DigestEmailModel(
            Recipient(profile, user.Email),
            profile.DisplayName,
            digest.LocalDate,
            listed,
            views.Count,
            reason,
            resetsOn,
            $"{prefs.ScheduleLocalTime:HH\\:mm} {profile.TimeZone}",
            links.Spa("/shortlist", new Dictionary<string, string?>()),
            id => links.Spa("/shortlist", new Dictionary<string, string?> { ["job"] = id.ToString() }),
            links.Spa("/preferences", new Dictionary<string, string?>()),
            links.Spa("/api/v1/email/unsubscribe", new Dictionary<string, string?> { ["token"] = unsubscribe.Create(userId) })));

        await sender.SendAsync(message, ct);
        digest.EmailedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(CancellationToken.None);
        LogSent(logger, digestId, listed.Count, views.Count);
    }

    /// <summary>
    /// The notification email when it is the sign-in email (verified by Identity) or was confirmed through its own link;
    /// otherwise the sign-in email (proposal 18).
    /// </summary>
    internal static string Recipient(UserProfile profile, string signInEmail) =>
        string.Equals(profile.NotificationEmail, signInEmail, StringComparison.OrdinalIgnoreCase) || profile.NotificationEmailVerifiedAt is not null
            ? profile.NotificationEmail
            : signInEmail;

    [LoggerMessage(Level = LogLevel.Information, Message = "Digest {DigestId} emailed: {Listed} listed of {Total}")]
    private static partial void LogSent(ILogger logger, Guid digestId, int listed, int total);

    [LoggerMessage(Level = LogLevel.Information, Message = "Digest {DigestId} email skipped ({Reason})")]
    private static partial void LogSkipped(ILogger logger, Guid digestId, string reason);
}
