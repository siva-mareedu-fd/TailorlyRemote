using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tailorly.App.Api;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;

namespace Tailorly.App.Features.Resumes;

/// <summary>
/// Skills the user confirmed during tailoring (ApiDesign §4.4). They persist across résumé versions and are the only
/// facts tailoring may add beyond the résumé itself. POST is idempotent on the lower-cased skill.
/// </summary>
public static class ConfirmedSkillsEndpoints
{
    public const int MaxSkills = 200;

    public static void MapConfirmedSkillsEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/me/confirmed-skills").WithTags("Resumes").RequireAuthorization(AuthPolicies.User);
        g.MapGet("", List).WithName("ListConfirmedSkills");
        g.MapPost("", Add).WithName("AddConfirmedSkill");
        g.MapDelete("/{id:guid}", Delete).WithName("DeleteConfirmedSkill");
    }

    private static async Task<Ok<List<ConfirmedSkillDto>>> List(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.UserConfirmedSkills.AsNoTracking().OrderBy(s => s.Skill).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ConfirmedSkillDto.From).ToList());
    }

    private static async Task<Results<Created<ConfirmedSkillDto>, Ok<ConfirmedSkillDto>>> Add(AddConfirmedSkillRequest body,
        AppDbContext db, ICurrentUser current, TimeProvider clock, CancellationToken ct)
    {
        var errors = new FieldErrors();
        var skill = errors.Text("skill", body.Skill, 100, label: "Skill");
        errors.ThrowIfAny();
        var key = UserConfirmedSkill.KeyOf(skill!);

        if (await db.UserConfirmedSkills.AsNoTracking().FirstOrDefaultAsync(s => s.SkillKey == key, ct) is { } existing)
            return TypedResults.Ok(ConfirmedSkillDto.From(existing));
        if (await db.UserConfirmedSkills.CountAsync(ct) >= MaxSkills)
            throw FieldErrors.Single("skill", $"You can confirm up to {MaxSkills} skills.");

        var row = new UserConfirmedSkill { UserId = current.UserId, Skill = skill!, SkillKey = key, CreatedAt = clock.GetUtcNow() };
        db.UserConfirmedSkills.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(row).State = EntityState.Detached;
            var raced = await db.UserConfirmedSkills.AsNoTracking().FirstAsync(s => s.SkillKey == key, ct);
            return TypedResults.Ok(ConfirmedSkillDto.From(raced));
        }
        return TypedResults.Created($"/api/v1/me/confirmed-skills/{row.Id}", ConfirmedSkillDto.From(row));
    }

    private static async Task<NoContent> Delete(Guid id, AppDbContext db, CancellationToken ct)
    {
        var deleted = await db.UserConfirmedSkills.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw new ApiException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found", "This skill is not in your list.");
        return TypedResults.NoContent();
    }
}
