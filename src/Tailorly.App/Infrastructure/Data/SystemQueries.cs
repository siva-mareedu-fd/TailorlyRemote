namespace Tailorly.App.Infrastructure.Data;

/// <summary>
/// The ONLY place allowed to call <c>IgnoreQueryFilters()</c> (DatabaseDesign §9). Each method must say which
/// system process uses it and why it must see all users. Keep this list short and review every addition.
///
/// Allowed callers: digest dispatcher (M3), purge job (M1), stuck-work sweep (M1), budget service (M1), admin queries (M6).
/// </summary>
public sealed class SystemQueries(AppDbContext db)
{
    // Intentionally empty in M0.
    internal AppDbContext Db => db;
}
