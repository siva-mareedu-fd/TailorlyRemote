using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>Two-step sign-in (TOTP via Identity) and the admin MFA requirement (SDD §14.2; PRD §17 "admin account secured").</summary>
[Collection(PostgresCollection.Name)]
public sealed class TwoFactorTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static async Task<string> EnableAsync(TestUser u)
    {
        var setup = await ApiClient.JsonAsync(await u.Api.PostAsync("/api/v1/me/two-factor/setup"));
        var key = setup.GetProperty("sharedKey").GetString()!;
        setup.GetProperty("authenticatorUri").GetString().Should().StartWith("otpauth://totp/Tailorly%20Remote").And.Contain("secret=");
        var res = await u.Api.PostAsync("/api/v1/me/two-factor/enable", new { code = Totp.Code(key, Now) });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(res));
        var codes = (await ApiClient.JsonAsync(res)).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        codes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        await u.Api.RefreshCsrfAsync();
        return key;
    }

    private static Task<HttpResponseMessage> LoginAsync(ApiClient api, TestUser u, string? code = null) =>
        api.PostAsync("/api/v1/auth/login", new { email = u.Email, password = u.Password, twoFactorCode = code });

    [Fact]
    public async Task Enabling_needs_a_right_code_and_sign_in_then_needs_one_too()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "mfa");
        var key = (await ApiClient.JsonAsync(await u.Api.PostAsync("/api/v1/me/two-factor/setup"))).GetProperty("sharedKey").GetString()!;
        var wrong = await u.Api.PostAsync("/api/v1/me/two-factor/enable", new { code = Totp.WrongCode(key, Now) });
        (wrong.StatusCode, await ApiClient.CodeAsync(wrong)).Should().Be((HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"));
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/me/two-factor"))).GetProperty("enabled").GetBoolean().Should().BeFalse();

        key = await EnableAsync(u);
        var status = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/me/two-factor"));
        (status.GetProperty("enabled").GetBoolean(), status.GetProperty("recoveryCodesLeft").GetInt32()).Should().Be((true, 10));
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/me"))).GetProperty("twoFactorEnabled").GetBoolean().Should().BeTrue();
        (await u.Api.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK, "the session that enabled it stays signed in");

        var fresh = await F.CreateApiClientAsync();
        var noCode = await LoginAsync(fresh, u);
        (noCode.StatusCode, await ApiClient.CodeAsync(noCode)).Should().Be((HttpStatusCode.Unauthorized, "MFA_REQUIRED"));
        (await fresh.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "no session without the code");

        var badCode = await LoginAsync(fresh, u, Totp.WrongCode(key, Now));
        (badCode.StatusCode, await ApiClient.CodeAsync(badCode)).Should().Be((HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"));

        var ok = await LoginAsync(fresh, u, Totp.Code(key, Now));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(ok));
        (await fresh.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_password_never_reveals_that_the_account_has_two_step_sign_in()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "mfa");
        await EnableAsync(u);
        var res = await (await F.CreateApiClientAsync()).PostAsync("/api/v1/auth/login", new { email = u.Email, password = "wrong password here" });
        (res.StatusCode, await ApiClient.CodeAsync(res)).Should().Be((HttpStatusCode.Unauthorized, "UNAUTHENTICATED"));
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "mfa");
        await EnableAsync(u);
        var codes = (await ApiClient.JsonAsync(await u.Api.PostAsync("/api/v1/me/two-factor/recovery-codes", new { password = u.Password })))
            .GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        codes.Should().HaveCount(10);

        (await LoginAsync(await F.CreateApiClientAsync(), u, codes[0])).StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoginAsync(await F.CreateApiClientAsync(), u, codes[0])).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
            "a recovery code is used up");
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/me/two-factor"))).GetProperty("recoveryCodesLeft").GetInt32().Should().Be(9);
    }

    [Fact]
    public async Task Turning_it_off_needs_the_password_and_removes_the_key()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "mfa");
        var key = await EnableAsync(u);
        (await u.Api.PostAsync("/api/v1/me/two-factor/disable", new { password = "not my password" })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
        (await u.Api.PostAsync("/api/v1/me/two-factor/disable", new { password = u.Password })).StatusCode.Should().Be(HttpStatusCode.OK);
        await u.Api.RefreshCsrfAsync();

        (await LoginAsync(await F.CreateApiClientAsync(), u)).StatusCode.Should().Be(HttpStatusCode.OK, "no code needed any more");
        var again = await ApiClient.JsonAsync(await u.Api.PostAsync("/api/v1/me/two-factor/setup"));
        again.GetProperty("sharedKey").GetString().Should().NotBe(key, "a later setup gets a new key");
    }

    [Fact]
    public async Task Wrong_codes_count_towards_lockout()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "mfa");
        var key = await EnableAsync(u);
        var api = await F.CreateApiClientAsync();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 5; i++) last = await LoginAsync(api, u, Totp.WrongCode(key, Now));
        (last.StatusCode, await ApiClient.CodeAsync(last)).Should().Be((HttpStatusCode.TooManyRequests, "ACCOUNT_LOCKED"));
        (await LoginAsync(api, u, Totp.Code(key, Now))).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "locked even with the right code");
    }

    [Fact]
    public async Task With_RequireMfa_admin_pages_need_two_step_sign_in()
    {
        await using var f = new TailorlyAppFactory(fx.ConnectionString, new Dictionary<string, string?> { ["Admin:RequireMfa"] = "true" });
        var admin = await TestUsers.CreateAdminAsync(f, "mfa-admin");

        var refused = await admin.Api.GetAsync("/api/v1/admin/status");
        (refused.StatusCode, await ApiClient.CodeAsync(refused)).Should().Be((HttpStatusCode.Forbidden, "MFA_NOT_ENABLED"));
        (await admin.Api.GetAsync("/admin/jobs")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the job dashboard too");
        (await admin.Api.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK, "the rest of the app still works");

        await EnableAsync(admin);
        (await admin.Api.GetAsync("/api/v1/admin/status")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Secrets_never_reach_logs_or_the_audit_log()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "mfa");
        var setup = await ApiClient.JsonAsync(await u.Api.PostAsync("/api/v1/me/two-factor/setup"));
        var key = setup.GetProperty("sharedKey").GetString()!;
        var code = Totp.Code(key, Now);
        var codes = (await ApiClient.JsonAsync(await u.Api.PostAsync("/api/v1/me/two-factor/enable", new { code })))
            .GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();

        var secrets = new[] { key.Replace(" ", "", StringComparison.Ordinal), key, code }.Concat(codes).ToList();
        var logs = string.Join('\n', F.Logs.Lines);
        var audit = string.Join("\n", await Db.StringsAsync(fx.ConnectionString,
            "SELECT action || coalesce(entity_id, '') || coalesce(meta::text, '') FROM audit_log WHERE actor_user_id = @u", ("u", u.Id)));
        foreach (var s in secrets)
        {
            logs.Should().NotContainEquivalentOf(s);
            audit.Should().NotContainEquivalentOf(s);
        }
        audit.Should().Contain("account.two_factor.enabled");
        JsonSerializer.Serialize(setup).Should().Contain("otpauth", "the URI goes to the user only");
    }
}
