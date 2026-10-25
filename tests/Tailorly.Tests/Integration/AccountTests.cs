using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Features.Accounts;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>ApiDesign §4.3 profile and consent, and account deletion end to end (SDD §7.5, DatabaseDesign §8).</summary>
[Collection(PostgresCollection.Name)]
public sealed class AccountTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;

    [Fact]
    public async Task Me_requires_sign_in()
    {
        var anon = await F.CreateApiClientAsync();
        var res = await anon.GetAsync("/api/v1/me");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiClient.CodeAsync(res)).Should().Be("UNAUTHENTICATED");
    }

    [Fact]
    public async Task Profile_can_be_read_and_updated_with_validation()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "profile", "Ravi");
        var me = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/me"));
        me.GetProperty("timeZone").GetString().Should().Be("Asia/Kolkata");
        me.GetProperty("notificationEmail").GetString().Should().Be(u.Email);
        me.GetProperty("accountStatus").GetString().Should().Be("active");

        var bad = await u.Api.PutAsync("/api/v1/me", new { displayName = "", timeZone = "Nowhere/City", notificationEmail = "x" });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.JsonAsync(bad)).GetProperty("errors").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("displayName", "timeZone", "notificationEmail");

        var ok = await u.Api.PutAsync("/api/v1/me", new { displayName = "Ravi K", timeZone = "Europe/London", notificationEmail = "alerts@example.test" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await ApiClient.JsonAsync(ok);
        updated.GetProperty("displayName").GetString().Should().Be("Ravi K");
        updated.GetProperty("timeZone").GetString().Should().Be("Europe/London");
        updated.GetProperty("email").GetString().Should().Be(u.Email, "the sign-in email does not change");
    }

    [Fact]
    public async Task Consent_is_recorded_for_the_current_version_only()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "reconsent");
        await Db.ExecAsync(fx.ConnectionString, "UPDATE user_profiles SET consent_version = NULL, consented_at = NULL WHERE user_id = @u", ("u", u.Id));
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/me"))).GetProperty("consent").ValueKind
            .Should().Be(System.Text.Json.JsonValueKind.Null);

        (await u.Api.PostAsync("/api/v1/me/consent", new { version = "1999-01" })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await u.Api.PostAsync("/api/v1/me/consent", new { version = TestUsers.ConsentVersion })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/me"))).GetProperty("consent").GetProperty("version").GetString()
            .Should().Be(TestUsers.ConsentVersion);
    }

    [Fact]
    public async Task Unverified_users_get_EMAIL_NOT_VERIFIED_on_protected_actions_but_can_read_me()
    {
        var api = await F.CreateApiClientAsync();
        var email = TestUsers.NewEmail("unverified");
        await TestUsers.RegisterAsync(F, api, email);
        await TestUsers.LoginAsync(api, email);

        (await api.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var res in new[]
                 {
                     await api.GetAsync("/api/v1/resumes"), await api.GetAsync("/api/v1/usage"),
                     await api.PutAsync("/api/v1/me", new { displayName = "x", timeZone = "Asia/Kolkata", notificationEmail = email }),
                     await api.DeleteAsync("/api/v1/account"),
                 })
        {
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ApiClient.CodeAsync(res)).Should().Be("EMAIL_NOT_VERIFIED");
        }
    }

    [Fact]
    public async Task Account_deletion_confirms_by_email_blocks_sign_in_and_purges_files_and_rows()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "delete");
        var resumeId = await TestUsers.UploadParsedResumeAsync(F, u);
        var storageKey = (await Db.StringsAsync(fx.ConnectionString, "SELECT storage_key FROM resumes WHERE id = @id", ("id", resumeId))).Single();
        var file = Path.Combine(F.StorageRoot, storageKey);
        File.Exists(file).Should().BeTrue();

        // 1. Request → 202 and a signed confirmation link by email
        var req = await u.Api.DeleteAsync("/api/v1/account");
        req.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await ApiClient.JsonAsync(req)).GetProperty("status").GetString().Should().Be("confirmation_sent");
        await F.Jobs.RunAllAsync();
        var mail = F.Emails.To(u.Email).Last(m => m.Subject.Contains("deleting", StringComparison.Ordinal));
        var (userId, token) = ApiClient.ParseLink(mail.TextBody, "/confirm-deletion");
        userId.Should().Be(u.Id);

        // 2. A tampered token is rejected
        var anon = await F.CreateApiClientAsync();
        (await anon.PostAsync("/api/v1/account/confirm-deletion", new { userId, token = token + "x" })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);

        // 3. Confirm → pending_deletion, sign-in blocked, the old session is refused, deletion log appended
        var confirm = await anon.PostAsync("/api/v1/account/confirm-deletion", new { userId, token });
        confirm.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Db.StringsAsync(fx.ConnectionString, "SELECT account_status FROM user_profiles WHERE user_id = @u", ("u", u.Id)))
            .Single().Should().Be("pending_deletion");

        var login = await anon.PostAsync("/api/v1/auth/login", new { email = u.Email, password = u.Password });
        login.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApiClient.CodeAsync(login)).Should().Be("ACCOUNT_DISABLED");
        var oldSession = await u.Api.GetAsync("/api/v1/me");
        oldSession.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApiClient.CodeAsync(oldSession)).Should().Be("ACCOUNT_DISABLED");

        File.ReadAllText(F.DeletionLogPath).Should().Contain(u.Id.ToString("D")).And.NotContain(u.Email);

        // 4. The purge job (enqueued on confirm) removes files and every row; audit rows carry no PII
        await F.Jobs.RunAllAsync();
        File.Exists(file).Should().BeFalse();
        Directory.Exists(Path.Combine(F.StorageRoot, u.Id.ToString("D"))).Should().BeFalse();
        foreach (var table in new[] { "asp_net_users WHERE id", "user_profiles WHERE user_id", "resumes WHERE user_id",
                     "resume_profiles WHERE user_id", "user_budget_counters WHERE user_id" })
            (await Db.ScalarAsync<long>(fx.ConnectionString, $"SELECT count(*) FROM {table} = @u", ("u", u.Id))).Should().Be(0, table);

        (await Db.ScalarAsync<long>(fx.ConnectionString, "SELECT count(*) FROM ai_usage WHERE user_id IS NULL AND task = 'resume_parse'"))
            .Should().BeGreaterThan(0, "usage rows stay as anonymised aggregates");
        var audit = await Db.StringsAsync(fx.ConnectionString,
            "SELECT action || ' ' || coalesce(entity_id,'') || ' ' || coalesce(meta::text,'') FROM audit_log WHERE entity_id = @id ORDER BY id",
            ("id", u.Id.ToString("D")));
        audit.Should().Contain(a => a.StartsWith("account.delete.requested", StringComparison.Ordinal))
            .And.Contain(a => a.StartsWith("account.delete.confirmed", StringComparison.Ordinal))
            .And.Contain(a => a.StartsWith("account.purged", StringComparison.Ordinal));
        audit.Should().NotContain(a => a.Contains(u.Email, StringComparison.OrdinalIgnoreCase));

        // 5. The link cannot be replayed into anything; purging again is a no-op
        (await anon.PostAsync("/api/v1/account/confirm-deletion", new { userId, token })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        F.Jobs.Clear();
    }

    [Fact]
    public async Task Hourly_purge_only_removes_accounts_pending_deletion()
    {
        var keep = await TestUsers.CreateVerifiedAsync(F, "keep");
        var gone = await TestUsers.CreateVerifiedAsync(F, "gone");
        await Db.ExecAsync(fx.ConnectionString, "UPDATE user_profiles SET account_status = 'pending_deletion' WHERE user_id = @u", ("u", gone.Id));

        await using (var scope = F.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<PurgeDeletedAccountsJob>().RunAsync(TestContext.Current.CancellationToken);

        (await Db.ScalarAsync<long>(fx.ConnectionString, "SELECT count(*) FROM asp_net_users WHERE id = @u", ("u", gone.Id))).Should().Be(0);
        (await Db.ScalarAsync<long>(fx.ConnectionString, "SELECT count(*) FROM asp_net_users WHERE id = @u", ("u", keep.Id))).Should().Be(1);
    }
}
