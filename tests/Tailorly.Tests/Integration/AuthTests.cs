using System.Net;
using FluentAssertions;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>ApiDesign §4.2: registration, verification, sign-in, lockout, password reset and change.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AuthTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;

    private static object RegisterBody(string email, string? consent = TestUsers.ConsentVersion, string password = TestUsers.Password,
        string? invite = null) =>
        new { email, password, displayName = "New User", timeZone = "Asia/Kolkata", consentVersion = consent, inviteCode = invite };

    [Fact]
    public async Task Register_is_uniform_for_existing_emails_and_mails_the_owner_instead()
    {
        var api = await F.CreateApiClientAsync();
        var email = TestUsers.NewEmail("dup");
        var firstId = await TestUsers.RegisterAsync(F, api, email);

        var again = await api.PostAsync("/api/v1/auth/register", RegisterBody(email.ToUpperInvariant()));
        again.StatusCode.Should().Be(HttpStatusCode.Created, "existence must not be revealed");
        (await ApiClient.JsonAsync(again)).GetProperty("userId").GetGuid().Should().NotBe(firstId);
        await F.Jobs.RunAllAsync();

        F.Emails.To(email).Select(m => m.Subject).Should().Contain(s => s.Contains("already have", StringComparison.Ordinal));
        (await Db.ScalarAsync<long>(fx.ConnectionString, "SELECT count(*) FROM asp_net_users WHERE normalized_email = @e",
            ("e", email.ToUpperInvariant()))).Should().Be(1);
    }

    [Fact]
    public async Task Register_validates_fields_consent_and_password_policy()
    {
        var api = await F.CreateApiClientAsync();

        var bad = await api.PostAsync("/api/v1/auth/register", new { email = "not-an-email", password = "", displayName = "" });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var errors = (await ApiClient.JsonAsync(bad)).GetProperty("errors");
        errors.TryGetProperty("email", out _).Should().BeTrue();
        errors.TryGetProperty("password", out _).Should().BeTrue();
        errors.TryGetProperty("displayName", out _).Should().BeTrue();

        var noConsent = await api.PostAsync("/api/v1/auth/register", RegisterBody(TestUsers.NewEmail(), consent: null));
        noConsent.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApiClient.CodeAsync(noConsent)).Should().Be("CONSENT_REQUIRED");

        var oldConsent = await api.PostAsync("/api/v1/auth/register", RegisterBody(TestUsers.NewEmail(), consent: "2020-01"));
        (await ApiClient.CodeAsync(oldConsent)).Should().Be("CONSENT_REQUIRED");

        var weak = await api.PostAsync("/api/v1/auth/register", RegisterBody(TestUsers.NewEmail(), password: "short"));
        weak.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.JsonAsync(weak)).GetProperty("errors").GetProperty("password").GetArrayLength().Should().BeGreaterThan(0);

        var badZone = await api.PostAsync("/api/v1/auth/register",
            new { email = TestUsers.NewEmail(), password = TestUsers.Password, displayName = "X", timeZone = "Mars/Base", consentVersion = TestUsers.ConsentVersion });
        (await ApiClient.JsonAsync(badZone)).GetProperty("errors").TryGetProperty("timeZone", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Weak_password_is_rejected_even_for_an_existing_email_so_existence_does_not_leak()
    {
        var api = await F.CreateApiClientAsync();
        var email = TestUsers.NewEmail("leak");
        await TestUsers.RegisterAsync(F, api, email);
        var res = await api.PostAsync("/api/v1/auth/register", RegisterBody(email, password: "short"));
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Consent_given_at_signup_is_recorded_with_version_time_and_ip_hash()
    {
        var api = await F.CreateApiClientAsync();
        var userId = await TestUsers.RegisterAsync(F, api, TestUsers.NewEmail("consent"));
        var row = await Db.StringsAsync(fx.ConnectionString,
            "SELECT consent_version || '|' || (consented_at IS NOT NULL) || '|' || coalesce(consent_ip_hash, 'null') FROM user_profiles WHERE user_id = @u",
            ("u", userId));
        row.Single().Should().StartWith(TestUsers.ConsentVersion + "|true|");
    }

    [Fact]
    public async Task The_legacy_India_zone_that_Chromium_reports_is_stored_canonically()
    {
        var api = await F.CreateApiClientAsync();
        var res = await api.PostAsync("/api/v1/auth/register", new
        {
            email = TestUsers.NewEmail("calcutta"), password = TestUsers.Password, displayName = "Z", timeZone = "Asia/Calcutta",
            consentVersion = TestUsers.ConsentVersion,
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var userId = (await ApiClient.JsonAsync(res)).GetProperty("userId").GetGuid();
        (await Db.StringsAsync(fx.ConnectionString, "SELECT time_zone FROM user_profiles WHERE user_id = @u", ("u", userId)))
            .Single().Should().Be("Asia/Kolkata");
        await F.Jobs.RunAllAsync();
    }

    [Fact]
    public async Task Invite_codes_are_required_and_consumed_when_the_flag_is_on()
    {
        var api = await F.CreateApiClientAsync();
        var code = "INV-" + Guid.NewGuid().ToString("N")[..8];
        await Db.ExecAsync(fx.ConnectionString, "INSERT INTO invite_codes (code, max_uses) VALUES (@c, 1)", ("c", code));
        await Db.ExecAsync(fx.ConnectionString, "UPDATE feature_flags SET value = 'true' WHERE key = 'signup.require_invite_code'");
        try
        {
            (await ApiClient.JsonAsync(await api.GetAsync("/api/v1/meta"))).GetProperty("requireInviteCode").GetBoolean().Should().BeTrue();

            var missing = await api.PostAsync("/api/v1/auth/register", RegisterBody(TestUsers.NewEmail()));
            (await ApiClient.CodeAsync(missing)).Should().Be("INVITE_CODE_REQUIRED");

            var wrong = await api.PostAsync("/api/v1/auth/register", RegisterBody(TestUsers.NewEmail(), invite: "NOPE"));
            (await ApiClient.CodeAsync(wrong)).Should().Be("INVITE_CODE_INVALID");

            var ok = await api.PostAsync("/api/v1/auth/register", RegisterBody(TestUsers.NewEmail(), invite: code));
            ok.StatusCode.Should().Be(HttpStatusCode.Created);

            var usedUp = await api.PostAsync("/api/v1/auth/register", RegisterBody(TestUsers.NewEmail(), invite: code));
            (await ApiClient.CodeAsync(usedUp)).Should().Be("INVITE_CODE_INVALID");
            (await Db.ScalarAsync<int>(fx.ConnectionString, "SELECT used_count FROM invite_codes WHERE code = @c", ("c", code))).Should().Be(1);
        }
        finally
        {
            await Db.ExecAsync(fx.ConnectionString, "UPDATE feature_flags SET value = 'false' WHERE key = 'signup.require_invite_code'");
        }
    }

    [Fact]
    public async Task Verify_email_rejects_bad_tokens_and_accepts_the_link_twice()
    {
        var api = await F.CreateApiClientAsync();
        var email = TestUsers.NewEmail("verify");
        var userId = await TestUsers.RegisterAsync(F, api, email);

        var bad = await api.PostAsync("/api/v1/auth/verify-email", new { userId, token = "bm90LWEtdG9rZW4" });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.JsonAsync(bad)).GetProperty("errors").TryGetProperty("token", out _).Should().BeTrue();

        await TestUsers.VerifyFromEmailAsync(F, api, email);
        await TestUsers.VerifyFromEmailAsync(F, api, email); // second click is fine
    }

    [Fact]
    public async Task Resend_verification_sends_a_new_link_only_while_unverified()
    {
        var api = await F.CreateApiClientAsync();
        var email = TestUsers.NewEmail("resend");
        await TestUsers.RegisterAsync(F, api, email);
        await TestUsers.LoginAsync(api, email);

        (await api.PostAsync("/api/v1/auth/resend-verification")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await F.Jobs.RunAllAsync();
        F.Emails.To(email).Count(m => m.Subject.Contains("Verify", StringComparison.Ordinal)).Should().Be(2);

        await TestUsers.VerifyFromEmailAsync(F, api, email);
        (await api.PostAsync("/api/v1/auth/resend-verification")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await F.Jobs.RunAllAsync();
        F.Emails.To(email).Count(m => m.Subject.Contains("Verify", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task Login_rejects_wrong_credentials_uniformly_and_locks_after_five_failures()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "lock");
        var api = await F.CreateApiClientAsync();

        var unknown = await api.PostAsync("/api/v1/auth/login", new { email = TestUsers.NewEmail("nobody"), password = "whatever-123" });
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiClient.CodeAsync(unknown)).Should().Be("UNAUTHENTICATED");

        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++)
            last = await api.PostAsync("/api/v1/auth/login", new { email = u.Email, password = "wrong password here" });
        last.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ApiClient.CodeAsync(last)).Should().Be("ACCOUNT_LOCKED");
        last.Headers.RetryAfter.Should().NotBeNull();

        var right = await api.PostAsync("/api/v1/auth/login", new { email = u.Email, password = u.Password });
        (await ApiClient.CodeAsync(right)).Should().Be("ACCOUNT_LOCKED", "the correct password does not bypass the lockout");
    }

    [Fact]
    public async Task Login_returns_me_sets_the_cookie_and_logout_ends_the_session()
    {
        var api = await F.CreateApiClientAsync();
        var email = TestUsers.NewEmail("session");
        await TestUsers.RegisterAsync(F, api, email, "Session User");

        var login = await api.PostAsync("/api/v1/auth/login", new { email, password = TestUsers.Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        login.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("__Host-tailorly=", StringComparison.Ordinal)
            && c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("secure", StringComparison.OrdinalIgnoreCase));
        var me = await ApiClient.JsonAsync(login);
        me.GetProperty("email").GetString().Should().Be(email);
        me.GetProperty("displayName").GetString().Should().Be("Session User");
        me.GetProperty("roles").GetArrayLength().Should().Be(0);

        (await api.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.PostAsync("/api/v1/auth/logout")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await api.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forgot_password_is_uniform_and_reset_sets_a_new_password()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "reset");
        var anon = await F.CreateApiClientAsync();

        (await anon.PostAsync("/api/v1/auth/forgot-password", new { email = TestUsers.NewEmail("ghost") })).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await anon.PostAsync("/api/v1/auth/forgot-password", new { email = u.Email })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await F.Jobs.RunAllAsync();

        var mail = F.Emails.To(u.Email).Last(m => m.Subject.Contains("Reset", StringComparison.Ordinal));
        var (userId, token) = ApiClient.ParseLink(mail.TextBody, "/reset-password");

        var bad = await anon.PostAsync("/api/v1/auth/reset-password", new { userId, token = "bm9wZQ", newPassword = "a brand new passphrase" });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var ok = await anon.PostAsync("/api/v1/auth/reset-password", new { userId, token, newPassword = "a brand new passphrase" });
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await anon.PostAsync("/api/v1/auth/login", new { email = u.Email, password = u.Password })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        await TestUsers.LoginAsync(anon, u.Email, "a brand new passphrase");

        var reused = await anon.PostAsync("/api/v1/auth/reset-password", new { userId, token, newPassword = "yet another passphrase" });
        reused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, "a reset link works once");
    }

    [Fact]
    public async Task Change_password_checks_the_current_one_and_keeps_the_session()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "change");

        var wrong = await u.Api.PostAsync("/api/v1/auth/change-password", new { currentPassword = "nope nope nope", newPassword = "another long passphrase" });
        wrong.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ApiClient.JsonAsync(wrong)).GetProperty("errors").TryGetProperty("currentPassword", out _).Should().BeTrue();

        var ok = await u.Api.PostAsync("/api/v1/auth/change-password", new { currentPassword = u.Password, newPassword = "another long passphrase" });
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await u.Api.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK, "this session stays signed in");

        var fresh = await F.CreateApiClientAsync();
        await TestUsers.LoginAsync(fresh, u.Email, "another long passphrase");
    }

    [Fact]
    public async Task Sixth_signup_per_hour_from_one_ip_is_rejected_with_429()
    {
        await using var f = new TailorlyAppFactory(fx.ConnectionString, relaxedRateLimits: false);
        var api = await f.CreateApiClientAsync();
        for (var i = 0; i < 5; i++)
            (await api.PostAsync("/api/v1/auth/register", RegisterBody(TestUsers.NewEmail("ip")))).StatusCode.Should().Be(HttpStatusCode.Created);

        var sixth = await api.PostAsync("/api/v1/auth/register", RegisterBody(TestUsers.NewEmail("ip")));
        sixth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ApiClient.CodeAsync(sixth)).Should().Be("RATE_LIMITED");
        sixth.Headers.Contains("Retry-After").Should().BeTrue();
    }

    [Fact]
    public async Task Fourth_signup_per_hour_for_one_email_is_rejected_with_429()
    {
        await using var f = new TailorlyAppFactory(fx.ConnectionString, new Dictionary<string, string?>
        {
            ["RateLimits:auth-register:PermitLimit"] = "100",
        }, relaxedRateLimits: false);
        var api = await f.CreateApiClientAsync();
        var email = TestUsers.NewEmail("per-email");
        for (var i = 0; i < 3; i++)
            (await api.PostAsync("/api/v1/auth/register", RegisterBody(email))).StatusCode.Should().Be(HttpStatusCode.Created);
        var fourth = await api.PostAsync("/api/v1/auth/register", RegisterBody(email.ToUpperInvariant()));
        fourth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        fourth.Headers.GetValues("RateLimit-Limit").Single().Should().Be("3");
    }
}
