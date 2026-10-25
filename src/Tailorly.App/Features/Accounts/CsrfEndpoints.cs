using Tailorly.App.Api;

namespace Tailorly.App.Features.Accounts;

/// <summary><c>GET /api/v1/auth/csrf</c> → 204 and sets the readable <c>XSRF-TOKEN</c> cookie (ApiDesign §4.2).</summary>
public static class CsrfEndpoints
{
    public static void MapCsrfEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/auth/csrf", (HttpContext http) =>
            {
                CsrfProtection.IssueTokens(http);
                return Results.NoContent();
            })
            .Produces(StatusCodes.Status204NoContent)
            .WithName("GetCsrfToken")
            .WithTags("Auth")
            .AllowAnonymous();
    }
}
