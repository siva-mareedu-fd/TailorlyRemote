using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>
/// M6 admin console API (ApiDesign §4.11; PRD F15): AI task config with its cache, feature flags, invite codes, the user
/// list (ids and counts only), suspension, usage, and the audit row of every call. Shared rows that a test changes are
/// put back in <c>finally</c>, because the collection shares one database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdminConsoleTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;
    private string Cs => fx.ConnectionString;

    [Fact]
    public async Task AI_config_lists_every_task_and_an_edit_applies_to_the_next_call_at_once()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        var list = await ApiClient.JsonAsync(await admin.Api.GetAsync("/api/v1/admin/ai-config"));
        list.EnumerateArray().Select(t => t.GetProperty("task").GetString()).Should().BeEquivalentTo(
            ["resume_parse", "job_enrichment", "byo_job_enrichment", "job_fit_score", "ats_analysis", "resume_tailor", "cover_letter",
             "interview_prep"]);
        var parse = list.EnumerateArray().Single(t => t.GetProperty("task").GetString() == "resume_parse");
        var original = parse.GetProperty("model").GetString()!;

        var user = await TestUsers.CreateVerifiedAsync(F);
        await TestUsers.UploadParsedResumeAsync(F, user); // fills the 60-second cache with the seeded model
        try
        {
            var res = await admin.Api.PutAsync("/api/v1/admin/ai-config/resume_parse", new { model = "claude-test-model", maxTokens = 3500 });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(res));
            var body = await ApiClient.JsonAsync(res);
            (body.GetProperty("model").GetString(), body.GetProperty("maxTokens").GetInt32(), body.GetProperty("updatedBy").GetGuid())
                .Should().Be(("claude-test-model", 3500, admin.Id));

            F.Ai.Requests.Clear();
            await TestUsers.UploadParsedResumeAsync(F, user, DocxBuilder.SampleResume("Second Version"));
            F.Ai.Requests.Should().Contain(r => r.Task == "resume_parse" && r.Model == "claude-test-model" && r.MaxTokens == 3500,
                "the edit drops the cached row, so the next call reads it");
        }
        finally
        {
            await admin.Api.PutAsync("/api/v1/admin/ai-config/resume_parse", new { model = original, maxTokens = 4000 });
        }
    }

    [Fact]
    public async Task AI_config_rejects_bad_values_and_unknown_tasks()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        var bad = await admin.Api.PutAsync("/api/v1/admin/ai-config/cover_letter",
            new { model = "<script>", maxTokens = 0, temperature = 1.5, bucket = "free" });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var fields = (await ApiClient.JsonAsync(bad)).GetProperty("errors");
        foreach (var f in new[] { "model", "maxTokens", "temperature", "bucket" })
            fields.TryGetProperty(f, out _).Should().BeTrue(f);
        (await admin.Api.PutAsync("/api/v1/admin/ai-config/no_such_task", new { enabled = false })).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Turning_on_the_invite_flag_makes_signup_need_a_code_that_an_admin_creates()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        try
        {
            (await admin.Api.PutAsync("/api/v1/admin/flags/signup.require_invite_code", new { value = "yes" })).StatusCode
                .Should().Be(HttpStatusCode.UnprocessableEntity, "a boolean flag takes true or false");
            var on = await admin.Api.PutAsync("/api/v1/admin/flags/signup.require_invite_code", new { value = true });
            on.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(on));
            (await ApiClient.JsonAsync(await (await F.CreateApiClientAsync()).GetAsync("/api/v1/meta")))
                .GetProperty("requireInviteCode").GetBoolean().Should().BeTrue();

            var api = await F.CreateApiClientAsync();
            var refused = await api.PostAsync("/api/v1/auth/register", new
            {
                email = TestUsers.NewEmail(), password = TestUsers.Password, displayName = "No Code", consentVersion = TestUsers.ConsentVersion,
            });
            (await ApiClient.CodeAsync(refused)).Should().Be("INVITE_CODE_REQUIRED");

            var created = await admin.Api.PostAsync("/api/v1/admin/invite-codes", new { maxUses = 2, expiresAt = DateTimeOffset.UtcNow.AddDays(7) });
            created.StatusCode.Should().Be(HttpStatusCode.Created, await ApiClient.TextAsync(created));
            var code = (await ApiClient.JsonAsync(created)).GetProperty("code").GetString()!;
            code.Should().MatchRegex("^[A-Z2-9]{5}-[A-Z2-9]{5}$");

            await TestUsers.RegisterAsync(F, api, TestUsers.NewEmail("invited"), inviteCode: code);
            var listed = (await ApiClient.JsonAsync(await admin.Api.GetAsync("/api/v1/admin/invite-codes")))
                .EnumerateArray().Single(c => c.GetProperty("code").GetString() == code);
            (listed.GetProperty("usedCount").GetInt32(), listed.GetProperty("maxUses").GetInt32()).Should().Be((1, 2));

            (await admin.Api.PostAsync("/api/v1/admin/invite-codes", new { code })).StatusCode
                .Should().Be(HttpStatusCode.UnprocessableEntity, "codes are unique");
            (await admin.Api.SendJsonAsync(HttpMethod.Delete, "/api/v1/admin/invite-codes", new { code })).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await admin.Api.SendJsonAsync(HttpMethod.Delete, "/api/v1/admin/invite-codes", new { code })).StatusCode.Should().Be(HttpStatusCode.NotFound);

            var audit = string.Join("\n", await Db.StringsAsync(Cs,
                "SELECT action || ' ' || coalesce(entity, '') || ' ' || coalesce(entity_id, '') || ' ' || coalesce(meta::text, '') FROM audit_log WHERE actor_user_id = @u",
                ("u", admin.Id)));
            audit.Should().Contain("admin.put /api/v1/admin/flags/{key} signup.require_invite_code").And.Contain("\"value\": \"true\"");
            audit.Should().Contain("admin.delete /api/v1/admin/invite-codes").And.NotContain(code, "invite codes are secrets");
            audit.Should().Contain("VALIDATION_FAILED", "refused calls are audited too");
        }
        finally
        {
            await admin.Api.PutAsync("/api/v1/admin/flags/signup.require_invite_code", new { value = false });
        }
    }

    [Fact]
    public async Task Flags_are_listed_and_unknown_flags_are_404()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        var flags = await ApiClient.JsonAsync(await admin.Api.GetAsync("/api/v1/admin/flags"));
        flags.EnumerateArray().Select(f => f.GetProperty("key").GetString())
            .Should().Contain(["signup.require_invite_code", "digest.email_enabled", "pdf_export.enabled"]);
        flags.EnumerateArray().Single(f => f.GetProperty("key").GetString() == "digest.email_enabled")
            .GetProperty("value").ValueKind.Should().Be(JsonValueKind.True);
        (await admin.Api.PutAsync("/api/v1/admin/flags/made.up", new { value = true })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_user_list_shows_ids_flags_and_counts_but_never_an_email()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        var user = await TestUsers.CreateVerifiedAsync(F, "listed");
        await TestUsers.UploadParsedResumeAsync(F, user);

        var page = await admin.Api.GetAsync("/api/v1/admin/users?pageSize=50");
        var text = await ApiClient.TextAsync(page);
        text.Should().NotContain("@", "no email address, of anyone, is in the list");
        text.Should().NotContainEquivalentOf("Test User", "no names either");

        var found = await ApiClient.JsonAsync(await admin.Api.GetAsync($"/api/v1/admin/users?q={Uri.EscapeDataString(user.Email.ToUpperInvariant())}"));
        found.GetProperty("total").GetInt32().Should().Be(1, "an exact email finds the account, in any case");
        var row = found.GetProperty("items")[0];
        (row.GetProperty("id").GetGuid(), row.GetProperty("status").GetString(), row.GetProperty("resumeCount").GetInt32(),
            row.GetProperty("emailVerified").GetBoolean(), row.GetProperty("isAdmin").GetBoolean())
            .Should().Be((user.Id, "active", 1, true, false));
        row.GetProperty("tokensUsedThisPeriod").GetInt64().Should().BePositive("the parse was charged to Documents");
        row.GetProperty("lastSignInAt").ValueKind.Should().Be(JsonValueKind.String);

        var partial = await ApiClient.JsonAsync(await admin.Api.GetAsync($"/api/v1/admin/users?q={Uri.EscapeDataString(user.Email[..8])}"));
        partial.GetProperty("total").GetInt32().Should().Be(0, "the list is not an email search");
        var byId = await ApiClient.JsonAsync(await admin.Api.GetAsync($"/api/v1/admin/users?q={user.Id}"));
        byId.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(user.Id);
    }

    [Fact]
    public async Task A_suspended_user_is_refused_everywhere_until_unsuspended()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        var user = await TestUsers.CreateVerifiedAsync(F, "suspend");

        var res = await admin.Api.PostAsync($"/api/v1/admin/users/{user.Id}/suspend");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(res));
        (await ApiClient.JsonAsync(res)).GetProperty("status").GetString().Should().Be("suspended");

        var me = await user.Api.GetAsync("/api/v1/me");
        (me.StatusCode, await ApiClient.CodeAsync(me)).Should().Be((HttpStatusCode.Forbidden, "ACCOUNT_DISABLED"));
        var login = await (await F.CreateApiClientAsync()).PostAsync("/api/v1/auth/login", new { email = user.Email, password = user.Password });
        (login.StatusCode, await ApiClient.CodeAsync(login)).Should().Be((HttpStatusCode.Forbidden, "ACCOUNT_DISABLED"));

        (await admin.Api.PostAsync($"/api/v1/admin/users/{user.Id}/suspend")).StatusCode.Should().Be(HttpStatusCode.OK, "idempotent");
        (await ApiClient.JsonAsync(await admin.Api.PostAsync($"/api/v1/admin/users/{user.Id}/unsuspend")))
            .GetProperty("status").GetString().Should().Be("active");
        await TestUsers.LoginAsync(user.Api, user.Email);
        (await user.Api.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.Api.PostAsync($"/api/v1/admin/users/{admin.Id}/suspend")).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity, "an admin cannot lock themselves out");
        (await admin.Api.PostAsync($"/api/v1/admin/users/{Guid.NewGuid()}/suspend")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var audit = await Db.StringsAsync(Cs, "SELECT meta::text FROM audit_log WHERE actor_user_id = @a AND entity_id = @u",
            ("a", admin.Id), ("u", user.Id.ToString("D")));
        audit.Should().Contain(m => m.Contains("\"to\": \"suspended\"")).And.Contain(m => m.Contains("\"to\": \"active\""));
    }

    [Fact]
    public async Task An_account_being_deleted_cannot_be_suspended_or_unsuspended()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        var user = await TestUsers.CreateVerifiedAsync(F, "deleting");
        await Db.ExecAsync(Cs, "UPDATE user_profiles SET account_status = 'pending_deletion' WHERE user_id = @u", ("u", user.Id));
        (await admin.Api.PostAsync($"/api/v1/admin/users/{user.Id}/suspend")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await admin.Api.PostAsync($"/api/v1/admin/users/{user.Id}/unsuspend")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Usage_totals_tasks_and_users_by_id()
    {
        var admin = await TestUsers.CreateAdminAsync(F);
        var user = await TestUsers.CreateVerifiedAsync(F, "usage");
        await TestUsers.UploadParsedResumeAsync(F, user);

        var usage = await ApiClient.JsonAsync(await admin.Api.GetAsync("/api/v1/admin/usage"));
        usage.GetProperty("period").GetString().Should().MatchRegex(@"^\d{4}-\d{2}$");
        usage.GetProperty("byTask").EnumerateArray().Should().Contain(t => t.GetProperty("task").GetString() == "resume_parse"
            && t.GetProperty("calls").GetInt32() > 0 && t.GetProperty("bucket").GetString() == "documents");
        usage.GetProperty("byUser").EnumerateArray().Should().Contain(u => u.GetProperty("userId").GetGuid() == user.Id
            && u.GetProperty("documentsTokens").GetInt64() > 0);
        (await admin.Api.GetAsync("/api/v1/admin/usage?period=2026-13")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await admin.Api.GetAsync("/api/v1/admin/usage?period=1999-01")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
