using System.Security.Claims;

namespace Tailorly.App.Infrastructure.Data;

/// <summary>The user every user-owned query is scoped to. <see cref="Guid.Empty"/> when unauthenticated (fail closed).</summary>
public interface ICurrentUser
{
    Guid UserId { get; }
}

/// <summary>
/// Resolves the user from the request principal. Background jobs that act for a user call
/// <see cref="ActAs"/> at job start, which takes precedence for the current async flow.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private static readonly AsyncLocal<Guid?> Override = new();

    public Guid UserId
    {
        get
        {
            if (Override.Value is { } forced) return forced;
            var id = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(id, out var g) ? g : Guid.Empty;
        }
    }

    /// <summary>Scopes the current async flow (e.g. a Hangfire job) to <paramref name="userId"/> until disposed.</summary>
    public static IDisposable ActAs(Guid userId)
    {
        var previous = Override.Value;
        Override.Value = userId;
        return new Restore(previous);
    }

    private sealed class Restore(Guid? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}
