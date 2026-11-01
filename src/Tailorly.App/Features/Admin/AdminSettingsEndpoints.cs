using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Ai;
using Tailorly.App.Api;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Budgets;

namespace Tailorly.App.Features.Admin;

/// <summary>
/// AI task config, feature flags and invite codes (ApiDesign §4.11; PRD F15, §6.2, D9). Model ids live only in
/// <c>ai_task_config</c>; an edit drops this process's cached row, and the cache never keeps one for more than 60 s
/// (<see cref="TaskConfigCache"/>). Audit rows carry field names and new values, never invite codes.
/// </summary>
public static partial class AdminSettingsEndpoints
{
    /// <summary>Flags whose value must be a JSON boolean (DatabaseDesign §5 seed).</summary>
    private static readonly HashSet<string> BooleanFlags =
        [FeatureFlag.SignupRequireInviteCode, FeatureFlag.DigestEmailEnabled, FeatureFlag.PdfExportEnabled];

    /// <summary>Unambiguous characters for generated codes (no 0/O, 1/I/L).</summary>
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static void MapAdminSettings(this RouteGroupBuilder admin)
    {
        admin.MapGet("/ai-config", ListAiConfig).WithName("AdminListAiConfig");
        admin.MapPut("/ai-config/{task}", UpdateAiConfig).WithName("AdminUpdateAiConfig");
        admin.MapGet("/flags", ListFlags).WithName("AdminListFlags");
        admin.MapPut("/flags/{key}", UpdateFlag).WithName("AdminUpdateFlag");
        admin.MapGet("/invite-codes", ListInviteCodes).WithName("AdminListInviteCodes");
        admin.MapPost("/invite-codes", CreateInviteCode).WithName("AdminCreateInviteCode");
        // The code goes in the body, never the path: request paths are logged, and a code is a credential.
        admin.MapDelete("/invite-codes", DeleteInviteCode).WithName("AdminDeleteInviteCode");
    }

    // ---------- AI task config ----------

    private static async Task<Ok<List<AiTaskConfigDto>>> ListAiConfig(AppDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.AiTaskConfig.AsNoTracking().OrderBy(c => c.Task).ToListAsync(ct)).Select(ToDto).ToList());

    private static AiTaskConfigDto ToDto(AiTaskConfig c) =>
        new(c.Task, c.Model, c.FallbackModel, c.MaxTokens, c.Temperature, c.Bucket, c.Enabled, c.UpdatedAt, c.UpdatedBy);

    /// <summary>Changes the fields that are not null; <c>fallbackModel: ""</c> clears it.</summary>
    private static async Task<Ok<AiTaskConfigDto>> UpdateAiConfig(string task, UpdateAiConfigRequest body, HttpContext http,
        AppDbContext db, TaskConfigCache cache, ICurrentUser current, TimeProvider clock, CancellationToken ct)
    {
        var c = await db.AiTaskConfig.FirstOrDefaultAsync(x => x.Task == task, ct) ?? throw NotFound("AI task");
        var errors = new FieldErrors();
        var model = body.Model?.Trim();
        var fallback = body.FallbackModel?.Trim();
        if (body.Model is not null && !ModelId().IsMatch(model!)) errors.Add("model", "Use a model id such as claude-sonnet-5-5.");
        if (fallback is { Length: > 0 } && !ModelId().IsMatch(fallback)) errors.Add("fallbackModel", "Use a model id, or leave it empty.");
        if (body.MaxTokens is < 1 or > 64_000) errors.Add("maxTokens", "Use 1 to 64,000 tokens.");
        if (body.Temperature is < 0 or > 1) errors.Add("temperature", "Use 0 to 1.");
        if (body.Bucket is not null && body.Bucket is not (BudgetBuckets.Discovery or BudgetBuckets.Documents or BudgetBuckets.Platform))
            errors.Add("bucket", "Choose discovery, documents or platform.");
        errors.ThrowIfAny();

        var changed = new List<string>();
        if (model is not null && model != c.Model) { c.Model = model; changed.Add("model"); }
        if (fallback is not null && (fallback.Length == 0 ? null : fallback) != c.FallbackModel)
        {
            c.FallbackModel = fallback.Length == 0 ? null : fallback;
            changed.Add("fallbackModel");
        }
        if (body.MaxTokens is { } max && max != c.MaxTokens) { c.MaxTokens = max; changed.Add("maxTokens"); }
        if (body.Temperature is { } t && t != c.Temperature) { c.Temperature = t; changed.Add("temperature"); }
        if (body.Bucket is { } bucket && bucket != c.Bucket) { c.Bucket = bucket; changed.Add("bucket"); }
        if (body.Enabled is { } on && on != c.Enabled) { c.Enabled = on; changed.Add("enabled"); }
        if (changed.Count > 0)
        {
            c.UpdatedAt = clock.GetUtcNow();
            c.UpdatedBy = current.UserId;
            await db.SaveChangesAsync(ct);
            cache.Invalidate(task);
        }
        http.AuditDetails(new { changed, model = c.Model, enabled = c.Enabled, bucket = c.Bucket });
        return TypedResults.Ok(ToDto(c));
    }

    // ---------- feature flags ----------

    private static async Task<Ok<List<FeatureFlagDto>>> ListFlags(AppDbContext db, CancellationToken ct) =>
        TypedResults.Ok((await db.FeatureFlags.AsNoTracking().OrderBy(f => f.Key).ToListAsync(ct))
            .Select(f => new FeatureFlagDto(f.Key, JsonDocument.Parse(f.Value).RootElement.Clone())).ToList());

    /// <summary>Existing flags only (they are seeded); the three seeded ones take a JSON boolean.</summary>
    private static async Task<Ok<FeatureFlagDto>> UpdateFlag(string key, UpdateFlagRequest body, HttpContext http, AppDbContext db,
        CancellationToken ct)
    {
        var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Key == key, ct) ?? throw NotFound("flag");
        if (body.Value is not { } value || value.ValueKind == JsonValueKind.Undefined)
            throw FieldErrors.Single("value", "Value is required.");
        if (BooleanFlags.Contains(key) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw FieldErrors.Single("value", "This flag is true or false.");
        var raw = value.GetRawText();
        if (raw.Length > 1000) throw FieldErrors.Single("value", "Use a value of 1,000 characters or fewer.");
        flag.Value = raw;
        await db.SaveChangesAsync(ct);
        http.AuditDetails(new { value = BooleanFlags.Contains(key) ? raw : "(set)" });
        return TypedResults.Ok(new FeatureFlagDto(flag.Key, value.Clone()));
    }

    // ---------- invite codes ----------

    private static async Task<Ok<List<InviteCodeDto>>> ListInviteCodes(AppDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.InviteCodes.AsNoTracking()
            .OrderBy(i => i.ExpiresAt == null).ThenBy(i => i.ExpiresAt).ThenBy(i => i.Code)
            .Select(i => new InviteCodeDto(i.Code, i.MaxUses, i.UsedCount, i.ExpiresAt))
            .ToListAsync(ct));

    /// <summary>Creates a code (a random <c>XXXXX-XXXXX</c> unless one is given), 1 to 1,000 uses, optional expiry.</summary>
    private static async Task<Created<InviteCodeDto>> CreateInviteCode(CreateInviteCodeRequest body, HttpContext http, AppDbContext db,
        TimeProvider clock, CancellationToken ct)
    {
        var errors = new FieldErrors();
        var code = string.IsNullOrWhiteSpace(body.Code) ? NewCode() : body.Code.Trim();
        if (!InviteCodeFormat().IsMatch(code)) errors.Add("code", "Use 6 to 40 characters: letters, digits or -.");
        var maxUses = body.MaxUses ?? 1;
        if (maxUses is < 1 or > 1000) errors.Add("maxUses", "Use 1 to 1,000 uses.");
        if (body.ExpiresAt is { } exp && exp <= clock.GetUtcNow()) errors.Add("expiresAt", "Choose a time in the future.");
        errors.ThrowIfAny();
        if (await db.InviteCodes.AnyAsync(i => i.Code == code, ct)) throw FieldErrors.Single("code", "This code already exists.");

        var invite = new InviteCode { Code = code, MaxUses = maxUses, ExpiresAt = body.ExpiresAt?.ToUniversalTime() };
        db.InviteCodes.Add(invite);
        await db.SaveChangesAsync(ct);
        http.AuditDetails(new { maxUses, expires = invite.ExpiresAt is not null });
        return TypedResults.Created("/api/v1/admin/invite-codes",
            new InviteCodeDto(invite.Code, invite.MaxUses, invite.UsedCount, invite.ExpiresAt));
    }

    private static async Task<NoContent> DeleteInviteCode([FromBody] DeleteInviteCodeRequest body, AppDbContext db, CancellationToken ct)
    {
        var code = body.Code?.Trim();
        if (string.IsNullOrEmpty(code)) throw FieldErrors.Single("code", "Code is required.");
        if (await db.InviteCodes.Where(i => i.Code == code).ExecuteDeleteAsync(ct) == 0) throw NotFound("invite code");
        return TypedResults.NoContent();
    }

    private static string NewCode()
    {
        Span<char> c = stackalloc char[11];
        for (var i = 0; i < c.Length; i++)
            c[i] = i == 5 ? '-' : CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(c);
    }

    private static ApiException NotFound(string what) =>
        new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found", $"This {what} does not exist.");

    /// <summary>Provider model ids: letters, digits and <c>. _ : @ / -</c>, up to 100 characters. Never fetched or resolved.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:@/\-]{0,99}$")]
    private static partial Regex ModelId();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-]{5,39}$")]
    private static partial Regex InviteCodeFormat();
}

public sealed record AiTaskConfigDto(
    string Task,
    string Model,
    string? FallbackModel,
    int MaxTokens,
    decimal Temperature,
    string Bucket,
    bool Enabled,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedBy);

/// <param name="Temperature">Stored for the record; not sent to the model (DECISIONS 2026-10-25).</param>
public sealed record UpdateAiConfigRequest(
    string? Model,
    string? FallbackModel,
    int? MaxTokens,
    decimal? Temperature,
    string? Bucket,
    bool? Enabled);

public sealed record FeatureFlagDto(string Key, JsonElement Value);

public sealed record UpdateFlagRequest(JsonElement? Value);

public sealed record InviteCodeDto(string Code, int MaxUses, int UsedCount, DateTimeOffset? ExpiresAt);

public sealed record CreateInviteCodeRequest(string? Code, int? MaxUses, DateTimeOffset? ExpiresAt);

public sealed record DeleteInviteCodeRequest(string? Code);
