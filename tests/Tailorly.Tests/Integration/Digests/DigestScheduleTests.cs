using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tailorly.App.Ai;
using Tailorly.App.Features.Digests;
using Tailorly.App.Infrastructure.Data;
using Tailorly.App.Infrastructure.Jobs;
using Tailorly.Core.Budgets;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.MatchingTestData;

namespace Tailorly.Tests.Integration.Digests;

/// <summary>
/// Its own database: the dispatcher looks at every user, so these tests must only ever see the users they create.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DigestScheduleCollection : ICollectionFixture<PostgresAppFixture>
{
    public const string Name = "postgres-digest-schedule";
}

/// <summary>
/// The scheduled digest (PRD F7, SDD §11.1): the dispatcher's due-users SQL at time-zone boundaries (IST, a DST zone,
/// a window crossing midnight), claim idempotency across ticks and instances, the run's retries and fallback, the
/// email (attribution, explanations, List-Unsubscribe), the one-click unsubscribe flow and the notification-email rule.
/// </summary>
[Collection(DigestScheduleCollection.Name)]
public sealed partial class DigestScheduleTests : IAsyncLifetime
{
    private readonly PostgresAppFixture _fx;
    private TailorlyAppFactory F => _fx.Factory;
    private string Cs => _fx.ConnectionString;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public DigestScheduleTests(PostgresAppFixture fx) => _fx = fx;

    public async ValueTask InitializeAsync()
    {
        F.Ai.ClearScript();
        F.Jobs.Clear();
        // Users of earlier tests in this collection must not be due at the instants a test dispatches.
        await Db.ExecAsync(Cs, "UPDATE user_preferences SET schedule_enabled = false");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static DateTimeOffset Utc(int month, int day, int hour, int minute, int year = 2026) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private async Task<TestUser> ScheduledUserAsync(string time, string? zone = null)
    {
        var u = await CreateMatcherAsync(F, NewRole(), "sched", p =>
        {
            p["scheduleEnabled"] = true;
            p["scheduleLocalTime"] = time;
        });
        if (zone is not null)
        {
            var res = await u.Api.PutAsync("/api/v1/me", new { displayName = "Test User", timeZone = zone, notificationEmail = u.Email });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(res));
        }
        return u;
    }

    private Task<List<string>> DigestDatesAsync(Guid userId) => Db.StringsAsync(Cs,
        "SELECT to_char(local_date, 'YYYY-MM-DD') FROM digests WHERE user_id = @u AND kind = 'scheduled' ORDER BY local_date", ("u", userId));

    private int EnqueuedRuns(TailorlyAppFactory f, Guid digestId) =>
        f.Jobs.History.Count(j => j.Type == typeof(PerUserRunJob) && (Guid)j.Args[0] == digestId);

    // ---------- dispatcher: time zones and windows ----------

    [Fact]
    public async Task Claims_once_at_the_users_local_time_in_IST_and_again_the_next_day()
    {
        var u = await ScheduledUserAsync("06:10");

        (await DispatchAsync(F, Utc(11, 5, 0, 39))).Should().BeEmpty("06:09 IST is before the schedule");
        var first = await DispatchAsync(F, Utc(11, 5, 0, 40));
        first.Should().ContainSingle();
        (await DispatchAsync(F, Utc(11, 5, 0, 45))).Should().BeEmpty("already claimed for 5 Nov");
        (await DispatchAsync(F, Utc(11, 5, 4, 30))).Should().BeEmpty();
        (await DispatchAsync(F, Utc(11, 6, 0, 41))).Should().ContainSingle("a new local day");
        (await DigestDatesAsync(u.Id)).Should().Equal("2026-11-05", "2026-11-06");
        EnqueuedRuns(F, first[0]).Should().Be(1);
    }

    [Fact]
    public async Task Two_ticks_and_two_instances_create_exactly_one_digest()
    {
        var u = await ScheduledUserAsync("07:20");
        var now = Utc(11, 7, 1, 55); // 07:25 IST
        await using var second = new TailorlyAppFactory(Cs); // another app instance on the same database

        var claims = await Task.WhenAll(DispatchAsync(F, now), DispatchAsync(F, now), DispatchAsync(second, now), DispatchAsync(second, now));
        claims.SelectMany(c => c).Should().ContainSingle();
        var id = claims.SelectMany(c => c).Single();
        (await DigestDatesAsync(u.Id)).Should().Equal("2026-11-07");
        (EnqueuedRuns(F, id) + EnqueuedRuns(second, id)).Should().Be(1, "only the instance that claimed the row enqueues the run");

        // The unique index is the guarantee even without the dispatcher's NOT EXISTS check.
        await using var scope = F.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<SystemQueries>()
            .ClaimScheduledDigestAsync(Guid.CreateVersion7(), u.Id, new DateOnly(2026, 11, 7), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Late_users_are_caught_up_within_four_hours_but_not_after()
    {
        var inside = await ScheduledUserAsync("08:00");
        (await DispatchAsync(F, Utc(11, 8, 6, 29))).Should().ContainSingle("11:59 IST is inside the 4-hour window");
        (await DigestDatesAsync(inside.Id)).Should().Equal("2026-11-08");

        await Db.ExecAsync(Cs, "UPDATE user_preferences SET schedule_enabled = false WHERE user_id = @u", ("u", inside.Id));
        var late = await ScheduledUserAsync("08:00");
        (await DispatchAsync(F, Utc(11, 9, 6, 30))).Should().BeEmpty("12:00 IST is past the window");
        (await DigestDatesAsync(late.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_DST_zone_follows_its_local_clock_across_the_change()
    {
        var u = await ScheduledUserAsync("07:05", "America/New_York");

        // 31 Oct 2026: EDT (UTC-4) → 07:05 local is 11:05Z.
        (await DispatchAsync(F, Utc(10, 31, 11, 4))).Should().BeEmpty();
        (await DispatchAsync(F, Utc(10, 31, 11, 5))).Should().ContainSingle();
        // 1 Nov 2026: clocks fall back at 02:00 → EST (UTC-5), so 07:05 local is 12:05Z.
        (await DispatchAsync(F, Utc(11, 1, 11, 5))).Should().BeEmpty("06:05 EST");
        (await DispatchAsync(F, Utc(11, 1, 12, 5))).Should().ContainSingle();
        // 2 Nov 2026: EST.
        (await DispatchAsync(F, Utc(11, 2, 12, 4))).Should().BeEmpty();
        (await DispatchAsync(F, Utc(11, 2, 12, 5))).Should().ContainSingle();
        (await DigestDatesAsync(u.Id)).Should().Equal("2026-10-31", "2026-11-01", "2026-11-02");
    }

    [Fact]
    public async Task A_window_crossing_midnight_belongs_to_the_day_of_the_schedule_time()
    {
        var u = await ScheduledUserAsync("23:30");
        (await DispatchAsync(F, Utc(11, 10, 17, 55))).Should().BeEmpty("23:25 IST on 10 Nov");
        (await DispatchAsync(F, Utc(11, 10, 18, 35))).Should().ContainSingle("00:05 IST on 11 Nov is inside 10 Nov's window");
        (await DispatchAsync(F, Utc(11, 10, 21, 0))).Should().BeEmpty("02:30 IST: 10 Nov is already claimed, 11 Nov's time has not come");
        (await DispatchAsync(F, Utc(11, 11, 22, 1))).Should().BeEmpty("03:31 IST on 12 Nov is past 11 Nov's window (23:30 + 4 h)");
        (await DigestDatesAsync(u.Id)).Should().Equal("2026-11-10");
    }

    [Fact]
    public async Task Only_eligible_users_are_dispatched()
    {
        var noResume = await TestUsers.CreateVerifiedAsync(F, "sched-noresume");
        await SetPreferencesAsync(noResume, p => { p["scheduleEnabled"] = true; p["scheduleLocalTime"] = "09:40"; });
        var off = await ScheduledUserAsync("09:40");
        await SetPreferencesAsync(off, p => p["scheduleEnabled"] = false);
        var disabled = await ScheduledUserAsync("09:40");
        await Db.ExecAsync(Cs, "UPDATE user_profiles SET account_status = 'suspended' WHERE user_id = @u", ("u", disabled.Id));
        var ok = await ScheduledUserAsync("09:40");

        var claimed = await DispatchAsync(F, Utc(11, 12, 4, 15)); // 09:45 IST
        claimed.Should().ContainSingle();
        (await DigestDatesAsync(ok.Id)).Should().ContainSingle();
        foreach (var u in new[] { noResume, off, disabled }) (await DigestDatesAsync(u.Id)).Should().BeEmpty();
    }

    // ---------- the run and the email ----------

    /// <summary>A user whose schedule time was a minute ago in IST (so retries fall inside the real 4-hour window).</summary>
    private async Task<(TestUser User, Guid Job, Guid Digest)> DueNowAsync(Action<Dictionary<string, object?>>? prefs = null,
        string source = "remoteok")
    {
        var r = await DueNowWithRoleAsync(prefs, source);
        return (r.User, r.Job, r.Digest);
    }

    private async Task<(TestUser User, Guid Job, Guid Digest, string Role)> DueNowWithRoleAsync(
        Action<Dictionary<string, object?>>? prefs = null, string source = "remoteok")
    {
        var india = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, india).AddMinutes(-1);
        var role = NewRole();
        var u = await CreateMatcherAsync(F, role, "due", p =>
        {
            p["scheduleEnabled"] = true;
            p["scheduleLocalTime"] = local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            prefs?.Invoke(p);
        });
        var job = await InsertJobAsync(F, $"Senior {role} Engineer", source: source);
        var claimed = await DispatchAsync(F, DateTimeOffset.UtcNow);
        claimed.Should().ContainSingle();
        return (u, job, claimed[0], role);
    }

    private async Task<System.Text.Json.JsonElement> DigestAsync(TestUser u, Guid id) =>
        await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/digests/{id}"));

    /// <summary>Digest emails sent to the user's sign-in address (only digests carry List-Unsubscribe).</summary>
    private List<Tailorly.Core.Ports.EmailMessage> DigestMails(TestUser u) =>
        F.Emails.To(u.Email).Where(m => m.ListUnsubscribeUrl is not null).ToList();

    [Fact]
    public async Task A_scheduled_digest_is_emailed_once_with_explained_matches_attribution_and_one_click_unsubscribe()
    {
        var (u, jobId, digestId) = await DueNowAsync();
        await F.Jobs.RunAllAsync();

        var digest = await DigestAsync(u, digestId);
        digest.GetProperty("kind").GetString().Should().Be("scheduled");
        digest.GetProperty("status").GetString().Should().Be("ready");
        digest.GetProperty("emailedAt").ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);
        Items(digest).Single().GetProperty("job").GetProperty("id").GetGuid().Should().Be(jobId);

        var mail = DigestMails(u).Should().ContainSingle().Subject;
        mail.Subject.Should().Be("1 new remote match for you · Tailorly Remote");
        mail.ListUnsubscribeUrl.Should().StartWith("https://tailorly.test/api/v1/email/unsubscribe?token=");
        var html = WebUtility.HtmlDecode(mail.HtmlBody);
        html.Should().Contain("Fit 9/10").And.Contain("Your .NET and PostgreSQL experience matches the core stack");
        html.Should().MatchRegex(@"<a href=""https://remoteok\.com/remote-jobs/[0-9a-f]+"" style=""[^""]*"">via Remote OK</a>");
        html.Should().Contain($"https://tailorly.test/shortlist?job={jobId}").And.Contain("Apply on boards.greenhouse.io");
        html.Should().NotContain("nofollow");

        // Re-sending is a no-op (emailed_at), and the dispatcher does not claim the day again.
        await using (var scope = F.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<DigestEmailJob>().SendAsync(digestId, u.Id, Ct);
        DigestMails(u).Should().ContainSingle();
        (await DispatchAsync(F, DateTimeOffset.UtcNow)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_email_job_given_another_users_id_sends_nothing_and_leaves_the_digest_unsent()
    {
        var (u, _, digestId) = await DueNowAsync();
        await F.Jobs.RunAllAsync();
        var intruder = await TestUsers.CreateVerifiedAsync(F, "email-b", "Email Intruder B");
        await Db.ExecAsync(Cs, "UPDATE digests SET emailed_at = NULL WHERE id = @d", ("d", digestId));
        var before = DigestMails(u).Count;
        before.Should().Be(1);

        await using (var scope = F.Services.CreateAsyncScope()) // A's digest id with B's user id
            await scope.ServiceProvider.GetRequiredService<DigestEmailJob>().SendAsync(digestId, intruder.Id, Ct);

        DigestMails(u).Should().HaveCount(before);
        F.Emails.To(intruder.Email).Should().NotContain(m => m.ListUnsubscribeUrl != null);
        (await DigestAsync(u, digestId)).GetProperty("emailedAt").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);

        await using (var scope = F.Services.CreateAsyncScope()) // the owner's own job still delivers it
            await scope.ServiceProvider.GetRequiredService<DigestEmailJob>().SendAsync(digestId, u.Id, Ct);
        DigestMails(u).Should().HaveCount(before + 1);
    }

    [Fact]
    public async Task One_click_unsubscribe_turns_the_digest_off_without_signing_in()
    {
        var (u, _, _) = await DueNowAsync();
        await F.Jobs.RunAllAsync();
        var url = new Uri(DigestMails(u).Single().ListUnsubscribeUrl!);
        var path = url.PathAndQuery;

        var anon = F.CreateHttpsClient(); // no cookies, no CSRF token: like a mail client
        var get = await anon.GetAsync(path, Ct);
        get.StatusCode.Should().Be(HttpStatusCode.Redirect);
        get.Headers.Location!.ToString().Should().StartWith("/unsubscribe?token=");
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/preferences"))).GetProperty("scheduleEnabled").GetBoolean()
            .Should().BeTrue("opening the link changes nothing");

        var oneClick = new FormUrlEncodedContent([new KeyValuePair<string, string>("List-Unsubscribe", "One-Click")]);
        var post = await anon.PostAsync(path, oneClick, Ct);
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync(Ct));
        (await post.Content.ReadAsStringAsync(Ct)).Should().Contain("\"done\":true");
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/preferences"))).GetProperty("scheduleEnabled").GetBoolean().Should().BeFalse();
        (await anon.PostAsync(path, null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "idempotent");

        (await anon.PostAsync("/api/v1/email/unsubscribe?token=forged", null, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await anon.PostAsync("/api/v1/email/unsubscribe", null, Ct)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await DispatchAsync(F, DateTimeOffset.UtcNow.AddDays(1))).Should().BeEmpty("the digest is off");
    }

    [Fact]
    public async Task Unclear_jobs_stay_out_of_the_email_unless_the_user_opts_in_and_an_empty_digest_sends_nothing()
    {
        var (u, _, digestId) = await DueNowAsync();
        Reply(F, AiTaskNames.JobEnrichment, Enrichment("unclear"));
        await F.Jobs.RunAllAsync();

        Items(await DigestAsync(u, digestId)).Should().ContainSingle("the app shows unclear jobs with a warning");
        DigestMails(u).Should().BeEmpty("nothing left for the email and digest_when_empty is off");
        (await DigestAsync(u, digestId)).GetProperty("emailedAt").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);

        await SetPreferencesAsync(u, p => p["showUnclearEligibility"] = true);
        await using (var scope = F.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<DigestEmailJob>().SendAsync(digestId, u.Id, Ct);
        WebUtility.HtmlDecode(DigestMails(u).Single().HtmlBody).Should().Contain("Eligibility unclear");
    }

    [Fact]
    public async Task Digest_when_empty_sends_a_no_matches_email()
    {
        var (u, jobId, _) = await DueNowAsync(p => p["digestWhenEmpty"] = true);
        await Db.ExecAsync(Cs, "UPDATE jobs SET closed_at = now() WHERE id = @j", ("j", jobId));
        await F.Jobs.RunAllAsync();
        DigestMails(u).Single().Subject.Should().StartWith("No new matches today");
    }

    [Fact]
    public async Task The_email_flag_turns_digest_emails_off()
    {
        var (u, _, digestId) = await DueNowAsync();
        await Db.ExecAsync(Cs, "UPDATE feature_flags SET value = 'false' WHERE key = 'digest.email_enabled'");
        try
        {
            await F.Jobs.RunAllAsync();
            (await DigestAsync(u, digestId)).GetProperty("status").GetString().Should().Be("ready");
            DigestMails(u).Should().BeEmpty();
        }
        finally
        {
            await Db.ExecAsync(Cs, "UPDATE feature_flags SET value = 'true' WHERE key = 'digest.email_enabled'");
        }
    }

    [Fact]
    public async Task A_failed_attempt_is_retried_inside_the_window_and_then_succeeds()
    {
        var (u, _, digestId) = await DueNowAsync();
        FailNext(F, AiTaskNames.JobEnrichment);
        await F.Jobs.RunAllAsync(); // runs the attempt, then the scheduled retry

        F.Jobs.History.Should().Contain(j => j.Type == typeof(PerUserRunJob) && (Guid)j.Args[0] == digestId,
            "the retry is a new job for the same digest");
        EnqueuedRuns(F, digestId).Should().Be(2);
        var digest = await DigestAsync(u, digestId);
        digest.GetProperty("status").GetString().Should().Be("ready");
        digest.GetProperty("degraded").GetBoolean().Should().BeFalse();
        (await Db.ScalarAsync<int>(Cs, "SELECT attempt FROM digests WHERE id = @d", ("d", digestId))).Should().Be(2);
        Items(digest).Single().GetProperty("job").GetProperty("score").GetProperty("fitScore").GetInt32().Should().Be(9);
        DigestMails(u).Should().ContainSingle();
    }

    [Fact]
    public async Task When_the_AI_stays_down_the_last_attempt_sends_a_rule_based_digest_with_a_notice()
    {
        var (u, jobId, digestId) = await DueNowAsync();
        FailNext(F, AiTaskNames.JobEnrichment, times: 4); // first attempt + 3 retries
        await F.Jobs.RunAllAsync();

        var digest = await DigestAsync(u, digestId);
        digest.GetProperty("status").GetString().Should().Be("ready");
        digest.GetProperty("degraded").GetBoolean().Should().BeTrue();
        (await Db.ScalarAsync<int>(Cs, "SELECT attempt FROM digests WHERE id = @d", ("d", digestId))).Should().Be(4);
        var item = Items(digest).Single().GetProperty("job");
        item.GetProperty("id").GetGuid().Should().Be(jobId);
        item.GetProperty("score").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        var html = WebUtility.HtmlDecode(DigestMails(u).Single().HtmlBody);
        html.Should().Contain("AI service was unavailable today").And.Contain("Not scored").And.Contain("via Remote OK");
    }

    [Fact]
    public async Task A_used_up_Discovery_budget_sends_a_rule_based_digest_with_the_reset_date()
    {
        var (u, _, digestId) = await DueNowAsync();
        await Db.ExecAsync(Cs, """
            INSERT INTO user_budget_counters (user_id, period_key, bucket, limit_tokens, used_tokens)
            VALUES (@u, to_char(now() AT TIME ZONE 'Asia/Kolkata', 'YYYY-MM'), 'discovery', 600000, 600000)
            """, ("u", u.Id));
        await F.Jobs.RunAllAsync();

        (await DigestAsync(u, digestId)).GetProperty("degraded").GetBoolean().Should().BeTrue();
        WebUtility.HtmlDecode(DigestMails(u).Single().HtmlBody).Should().MatchRegex(@"Discovery budget for this month is used up until \d{4}-\d{2}-01");
    }

    [Fact]
    public async Task The_sweep_fails_a_run_stuck_for_30_minutes()
    {
        var (u, _, digestId) = await DueNowAsync();
        F.Jobs.Clear(); // the run never starts
        await Db.ExecAsync(Cs, "UPDATE digests SET created_at = now() - interval '31 minutes' WHERE id = @d", ("d", digestId));
        await using (var scope = F.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<Tailorly.App.Features.Resumes.SweepStuckWorkJob>().RunAsync(Ct);

        var digest = await DigestAsync(u, digestId);
        digest.GetProperty("status").GetString().Should().Be("failed");
        digest.GetProperty("errorCode").GetString().Should().Be("TIMEOUT");
    }

    // ---------- notification email (proposal 18) ----------

    [Fact]
    public async Task A_changed_notification_email_gets_digests_only_after_it_is_confirmed()
    {
        var (u, _, _, role) = await DueNowWithRoleAsync();
        var other = TestUsers.NewEmail("alerts");
        var put = await u.Api.PutAsync("/api/v1/me", new { displayName = "Test User", timeZone = "Asia/Kolkata", notificationEmail = other });
        (await ApiClient.JsonAsync(put)).GetProperty("notificationEmailVerified").GetBoolean().Should().BeFalse();
        await F.Jobs.RunAllAsync();

        DigestMails(u).Should().ContainSingle("until confirmed, the digest goes to the sign-in email");
        var confirm = F.Emails.To(other).Should().ContainSingle().Subject;
        confirm.Subject.Should().Contain("Confirm the email");
        var token = LinkToken().Match(confirm.TextBody).Groups[1].Value;

        var anon = await F.CreateApiClientAsync();
        (await anon.PostAsync("/api/v1/me/notification-email/verify", new { token = Uri.UnescapeDataString(token) }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/me"))).GetProperty("notificationEmailVerified").GetBoolean().Should().BeTrue();
        (await anon.PostAsync("/api/v1/me/notification-email/verify", new { token = "forged" })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);

        // The next day's digest goes to the confirmed address.
        await Db.ExecAsync(Cs, "UPDATE digests SET local_date = local_date - 1 WHERE user_id = @u", ("u", u.Id));
        await InsertJobAsync(F, $"{role} Engineer II");
        (await DispatchAsync(F, DateTimeOffset.UtcNow)).Should().ContainSingle();
        await F.Jobs.RunAllAsync();
        F.Emails.To(other).Should().Contain(m => m.ListUnsubscribeUrl != null);
    }

    // ---------- budgets: month rollover in Asia/Kolkata ----------

    [Fact]
    public async Task The_discovery_budget_resets_at_midnight_IST_on_the_first_of_the_month()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "rollover");
        await Db.ExecAsync(Cs, """
            INSERT INTO user_budget_counters (user_id, period_key, bucket, limit_tokens, used_tokens)
            VALUES (@u, '2026-10', 'discovery', 600000, 600000)
            """, ("u", u.Id));
        await using var scope = F.Services.CreateAsyncScope();
        var system = scope.ServiceProvider.GetRequiredService<SystemQueries>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<BudgetOptions>>();

        var before = new BudgetService(system, options, new FixedTimeProvider(Utc(10, 31, 18, 29).AddSeconds(59))); // 23:59:59 IST
        (await before.GetAsync(u.Id, BudgetBuckets.Discovery, Ct)).IsExhausted.Should().BeTrue();
        before.ResetsOn.Should().Be(new DateOnly(2026, 11, 1));

        var after = new BudgetService(system, options, new FixedTimeProvider(Utc(10, 31, 18, 30))); // 00:00 IST, 1 Nov
        after.CurrentPeriod.Should().Be("2026-11");
        var fresh = await after.GetAsync(u.Id, BudgetBuckets.Discovery, Ct);
        fresh.IsExhausted.Should().BeFalse();
        fresh.Remaining.Should().Be(600_000);
    }

    [GeneratedRegex(@"[?&]token=([^&\s]+)")]
    private static partial Regex LinkToken();
}
