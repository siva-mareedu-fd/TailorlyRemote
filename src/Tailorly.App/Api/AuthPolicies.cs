using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;

namespace Tailorly.App.Api;

/// <summary>
/// Endpoint policies (ApiDesign §5, SDD §14.1):
/// <list type="bullet">
/// <item><see cref="UserAny"/> (<c>user*</c>): signed in and the account is active.</item>
/// <item><see cref="User"/> (<c>user</c>): <c>user*</c> plus a verified email.</item>
/// <item><see cref="Admin"/>: <c>user</c> plus the Admin role, and two-step sign-in turned on while <c>Admin:RequireMfa</c>
/// is true (SDD §14.2 "MFA recommended (TOTP via Identity)").</item>
/// </list>
/// Failures become problem details with a specific code (<c>ACCOUNT_DISABLED</c>, <c>EMAIL_NOT_VERIFIED</c>,
/// <c>MFA_NOT_ENABLED</c>).
/// </summary>
public static class AuthPolicies
{
    public const string Admin = "Admin";
    public const string User = "User";
    public const string UserAny = "UserAny";

    public static void AddTailorlyAuthorization(this IServiceCollection services)
    {
        services.AddScoped<AccountState>();
        services.AddScoped<IAuthorizationHandler, AccountRequirementHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(UserAny, p => p.RequireAuthenticatedUser().AddRequirements(new AccountRequirement(RequireVerifiedEmail: false)))
            .AddPolicy(User, p => p.RequireAuthenticatedUser().AddRequirements(new AccountRequirement(RequireVerifiedEmail: true)))
            .AddPolicy(Admin, p => p.RequireAuthenticatedUser().RequireRole(AppRole.Admin)
                .AddRequirements(new AccountRequirement(RequireVerifiedEmail: true, AdminMfa: true)));
    }
}

/// <param name="AdminMfa">Also require two-step sign-in when <c>Admin:RequireMfa</c> is on.</param>
public sealed record AccountRequirement(bool RequireVerifiedEmail, bool AdminMfa = false) : IAuthorizationRequirement;

/// <summary>The signed-in user's account flags, loaded once per request.</summary>
public sealed class AccountState(AppDbContext db, ICurrentUser currentUser)
{
    public sealed record Snapshot(bool EmailConfirmed, string AccountStatus, string? ConsentVersion, bool TwoFactorEnabled);

    private Snapshot? _snapshot;
    private bool _loaded;

    public async Task<Snapshot?> GetAsync(CancellationToken ct)
    {
        if (_loaded) return _snapshot;
        var id = currentUser.UserId;
        _snapshot = id == Guid.Empty
            ? null
            : await (from u in db.Users.AsNoTracking()
                     join p in db.UserProfiles.AsNoTracking() on u.Id equals p.UserId
                     where u.Id == id
                     select new Snapshot(u.EmailConfirmed, p.AccountStatus, p.ConsentVersion, u.TwoFactorEnabled)).FirstOrDefaultAsync(ct);
        _loaded = true;
        return _snapshot;
    }
}

internal sealed class AccountRequirementHandler(IConfiguration config) : AuthorizationHandler<AccountRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AccountRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true || context.Resource is not HttpContext http) return;
        var state = await http.RequestServices.GetRequiredService<AccountState>().GetAsync(http.RequestAborted);

        if (state is null || state.AccountStatus != AccountStatus.Active)
            context.Fail(new AuthorizationFailureReason(this, ErrorCodes.AccountDisabled));
        else if (requirement.RequireVerifiedEmail && !state.EmailConfirmed)
            context.Fail(new AuthorizationFailureReason(this, ErrorCodes.EmailNotVerified));
        else if (requirement.AdminMfa && !state.TwoFactorEnabled && config.GetValue("Admin:RequireMfa", true))
            context.Fail(new AuthorizationFailureReason(this, ErrorCodes.MfaNotEnabled));
        else
            context.Succeed(requirement);
    }
}

/// <summary>Turns a failed account requirement into the matching 403 problem; everything else uses the default handler.</summary>
internal sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext http, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden && result.AuthorizationFailure?.FailureReasons.FirstOrDefault()?.Message is { } code)
        {
            return code switch
            {
                ErrorCodes.AccountDisabled => Problems.WriteAsync(http, StatusCodes.Status403Forbidden, code,
                    "Account disabled", "This account is being deleted or has been suspended."),
                ErrorCodes.EmailNotVerified => Problems.WriteAsync(http, StatusCodes.Status403Forbidden, code,
                    "Verify your email first", "Open the link we emailed you, or ask for a new one."),
                ErrorCodes.MfaNotEnabled => Problems.WriteAsync(http, StatusCodes.Status403Forbidden, code,
                    "Turn on two-step sign-in", "Admin pages need two-step sign-in. Turn it on in Account, then try again."),
                _ => _default.HandleAsync(next, http, policy, result),
            };
        }
        return _default.HandleAsync(next, http, policy, result);
    }
}
