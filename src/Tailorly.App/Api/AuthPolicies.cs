using Tailorly.App.Infrastructure.Data;

namespace Tailorly.App.Api;

/// <summary>Endpoint policies (SDD §14.1). Verified-email and account-status checks join the "user" policy in M1.</summary>
public static class AuthPolicies
{
    public const string Admin = "Admin";

    public static void AddTailorlyAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(Admin, p => p.RequireAuthenticatedUser().RequireRole(AppRole.Admin));
    }
}
