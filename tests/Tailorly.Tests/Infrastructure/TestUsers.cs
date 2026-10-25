using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.Tests.Infrastructure;

public sealed record TestUser(ApiClient Api, Guid Id, string Email, string Password);

/// <summary>Journey A building blocks: register → verify (via the captured email) → sign in.</summary>
public static class TestUsers
{
    public const string Password = "correct horse battery staple";
    public const string ConsentVersion = "2026-10-1";

    public static string NewEmail(string tag = "user") => $"{tag}-{Guid.NewGuid():N}@example.test";

    public static async Task<Guid> RegisterAsync(TailorlyAppFactory f, ApiClient api, string email, string name = "Test User",
        string? inviteCode = null)
    {
        var res = await api.PostAsync("/api/v1/auth/register", new
        {
            email,
            password = Password,
            displayName = name,
            timeZone = "Asia/Kolkata",
            consentVersion = ConsentVersion,
            inviteCode,
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await ApiClient.TextAsync(res));
        await f.Jobs.RunAllAsync();
        return (await ApiClient.JsonAsync(res)).GetProperty("userId").GetGuid();
    }

    public static async Task VerifyFromEmailAsync(TailorlyAppFactory f, ApiClient api, string email)
    {
        var mail = f.Emails.To(email).Last(m => m.Subject.Contains("Verify", StringComparison.Ordinal));
        var (userId, token) = ApiClient.ParseLink(mail.TextBody, "/verify-email");
        var res = await api.PostAsync("/api/v1/auth/verify-email", new { userId, token });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, await ApiClient.TextAsync(res));
    }

    public static async Task LoginAsync(ApiClient api, string email, string password = Password)
    {
        var res = await api.PostAsync("/api/v1/auth/login", new { email, password });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(res));
    }

    /// <summary>A signed-in user with a verified email and recorded consent (consent is given at sign-up).</summary>
    public static async Task<TestUser> CreateVerifiedAsync(TailorlyAppFactory f, string tag = "user", string name = "Test User")
    {
        var api = await f.CreateApiClientAsync();
        var email = NewEmail(tag);
        var id = await RegisterAsync(f, api, email, name);
        await VerifyFromEmailAsync(f, api, email);
        await LoginAsync(api, email);
        return new TestUser(api, id, email, Password);
    }

    /// <summary>A verified user with the Admin role (signed in again so the cookie carries the role).</summary>
    public static async Task<TestUser> CreateAdminAsync(TailorlyAppFactory f, string tag = "admin")
    {
        var u = await CreateVerifiedAsync(f, tag, "Test Admin");
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await users.FindByIdAsync(u.Id.ToString());
            (await users.AddToRoleAsync(user!, AppRole.Admin)).Succeeded.Should().BeTrue();
        }
        await LoginAsync(u.Api, u.Email);
        await u.Api.RefreshCsrfAsync();
        return u;
    }

    /// <summary>Uploads a résumé and runs the parse job; returns the résumé id.</summary>
    public static async Task<Guid> UploadParsedResumeAsync(TailorlyAppFactory f, TestUser u, byte[]? docx = null,
        string fileName = "resume.docx")
    {
        var res = await u.Api.UploadAsync("/api/v1/resumes", fileName, docx ?? DocxBuilder.SampleResume());
        res.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(res));
        var id = (await ApiClient.JsonAsync(res)).GetProperty("id").GetGuid();
        await f.Jobs.RunAllAsync();
        return id;
    }
}
