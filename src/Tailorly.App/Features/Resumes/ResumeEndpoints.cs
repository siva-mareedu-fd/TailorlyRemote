using System.Security.Cryptography;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tailorly.App.Ai;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Api;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core;
using Tailorly.Core.Budgets;
using Tailorly.Core.Ports;
using Tailorly.Docx;

namespace Tailorly.App.Features.Resumes;

/// <summary>
/// Résumé upload, versions, download, activation, deletion and the parsed profile (ApiDesign §4.4). Every <c>{id}</c> is
/// resolved through the user-scoped context, so another user's résumé is a 404. File names and content are never logged.
/// </summary>
public static partial class ResumeEndpoints
{
    /// <summary>5 MB file plus room for multipart framing.</summary>
    private const long MaxUploadRequestBytes = DocxLimits.MaxCompressedBytes + 64 * 1024;

    public static void MapResumeEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/resumes").WithTags("Resumes").RequireAuthorization(AuthPolicies.User);
        g.MapPost("", Upload).WithName("UploadResume").RequireRateLimiting(RateLimiting.AiStart)
            .Accepts<ResumeUploadForm>("multipart/form-data");
        g.MapGet("", List).WithName("ListResumes");
        g.MapGet("/{id:guid}", Get).WithName("GetResume");
        g.MapGet("/{id:guid}/download", Download).WithName("DownloadResume").RequireRateLimiting(RateLimiting.Download)
            .Produces(StatusCodes.Status200OK, contentType: DocxContentType);
        g.MapPost("/{id:guid}/activate", Activate).WithName("ActivateResume");
        g.MapDelete("/{id:guid}", Delete).WithName("DeleteResume");
        g.MapGet("/{id:guid}/profile", GetProfile).WithName("GetResumeProfile");
        g.MapPut("/{id:guid}/profile", UpdateProfile).WithName("UpdateResumeProfile");
    }

    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Not found", "This résumé does not exist.");

    // ---------- upload ----------

    private static async Task<Results<Accepted<ResumeDto>, Ok<ResumeDto>>> Upload(HttpContext http, AppDbContext db,
        AccountState account, IConfiguration config, BudgetService budgets, ICurrentUser current, IFileStore files,
        IBackgroundJobClient jobs, ILoggerFactory loggers, CancellationToken ct)
    {
        var userId = current.UserId;
        var state = await account.GetAsync(ct);
        if (state?.ConsentVersion != AuthEndpoints.CurrentConsentVersion(config)) throw AuthEndpoints.ConsentRequired();
        await ThrowIfBudgetExhausted(budgets, userId, ct);

        // A parse already in flight: a repeated submission returns it (ApiDesign §1, duplicate submissions).
        if (await InFlightAsync(db, ct) is { } inflight)
            return TypedResults.Ok(ResumeDto.From(inflight));

        var (fileName, bytes) = await ReadUploadAsync(http, ct);
        var inspection = DocxSafetyInspector.Inspect(fileName, bytes);
        var log = loggers.CreateLogger(typeof(ResumeEndpoints));
        if (!inspection.IsOk)
        {
            LogRejected(log, inspection.Reason, bytes.Length);
            throw inspection.Verdict switch
            {
                DocxVerdict.TooLarge => TooLarge(),
                DocxVerdict.UnsupportedType => new ApiException(StatusCodes.Status415UnsupportedMediaType,
                    ErrorCodes.UnsupportedFileType, "Unsupported file type", inspection.Message,
                    new Dictionary<string, object?> { ["reason"] = inspection.Reason }),
                _ => new ApiException(StatusCodes.Status422UnprocessableEntity, ErrorCodes.FileUnsafe,
                    "File rejected for safety", inspection.Message, new Dictionary<string, object?> { ["reason"] = inspection.Reason }),
            };
        }

        var resume = new Resume
        {
            UserId = userId,
            OriginalFilename = SafeFileName(fileName),
            SizeBytes = bytes.Length,
            StorageKey = "",
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ParseStatus = WorkStatus.Queued,
        };
        resume.StorageKey = Resume.KeyFor(userId, resume.Id);

        using (var content = new MemoryStream(bytes, writable: false))
            await files.SaveAsync(resume.StorageKey, content, ct);

        for (var attempt = 0; ; attempt++)
        {
            resume.Version = (await db.Resumes.MaxAsync(r => (int?)r.Version, ct) ?? 0) + 1;
            db.Resumes.Add(resume);
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
            {
                db.Entry(resume).State = EntityState.Detached;
                if (pg.ConstraintName == "ux_resumes_parse_inflight" && await InFlightAsync(db, ct) is { } raced)
                {
                    await files.DeletePrefixAsync(Resume.PrefixFor(userId, resume.Id), CancellationToken.None);
                    return TypedResults.Ok(ResumeDto.From(raced));
                }
                if (attempt >= 3) throw;
            }
        }

        jobs.Enqueue<ParseResumeJob>(j => j.RunAsync(resume.Id, userId, null, CancellationToken.None));
        LogUploaded(log, resume.Id, resume.Version, resume.SizeBytes);
        return TypedResults.Accepted($"/api/v1/resumes/{resume.Id}", ResumeDto.From(resume));
    }

    private static Task<Resume?> InFlightAsync(AppDbContext db, CancellationToken ct) =>
        db.Resumes.AsNoTracking().FirstOrDefaultAsync(r => r.ParseStatus == WorkStatus.Queued || r.ParseStatus == WorkStatus.Running, ct);

    internal static async Task ThrowIfBudgetExhausted(BudgetService budgets, Guid userId, CancellationToken ct)
    {
        try
        {
            await budgets.EnsureRemainingAsync(userId, BudgetBuckets.Documents, ct);
        }
        catch (BudgetExceededException ex)
        {
            throw BudgetProblem(ex);
        }
    }

    internal static ApiException BudgetProblem(BudgetExceededException ex) => new(StatusCodes.Status409Conflict,
        ErrorCodes.BudgetExceeded, "Monthly AI budget used up",
        $"Your {Capitalize(ex.Bucket)} budget resets on {BudgetPolicy.FormatResetsOn(ex.ResetsOn)}.",
        new Dictionary<string, object?> { ["bucket"] = ex.Bucket, ["resetsOn"] = BudgetPolicy.FormatResetsOn(ex.ResetsOn) });

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static ApiException TooLarge() => new(StatusCodes.Status413PayloadTooLarge, ErrorCodes.FileTooLarge,
        "File too large", "The file is larger than 5 MB.");

    private static async Task<(string FileName, byte[] Bytes)> ReadUploadAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = MaxUploadRequestBytes;
        if (http.Request.ContentLength > MaxUploadRequestBytes) throw TooLarge();
        if (!http.Request.HasFormContentType)
            throw new ApiException(StatusCodes.Status415UnsupportedMediaType, ErrorCodes.UnsupportedFileType,
                "Unsupported media type", "Upload the file as multipart/form-data in a field named \"file\".");

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = MaxUploadRequestBytes }, ct);
        }
        catch (Exception ex) when (ex is BadHttpRequestException { StatusCode: 413 } or InvalidDataException)
        {
            throw TooLarge();
        }

        var file = form.Files.GetFile("file") ?? throw FieldErrors.Single("file", "Choose a .docx file to upload.");
        if (file.Length == 0) throw FieldErrors.Single("file", "The file is empty.");
        if (file.Length > DocxLimits.MaxCompressedBytes) throw TooLarge();
        using var ms = new MemoryStream((int)file.Length);
        await file.CopyToAsync(ms, ct);
        return (file.FileName, ms.ToArray());
    }

    /// <summary>Keeps the user's file name for display and downloads, without paths or control characters.</summary>
    internal static string SafeFileName(string? name)
    {
        var n = Path.GetFileName(name ?? "").Trim();
        n = new string(n.Where(c => !char.IsControl(c) && c is not '"' and not '\\' and not '/').ToArray());
        if (n.Length == 0) n = "resume.docx";
        return n.Length <= 200 ? n : n[..(200 - 5)] + ".docx";
    }

    // ---------- read ----------

    private static async Task<Ok<List<ResumeDto>>> List(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.Resumes.AsNoTracking().OrderByDescending(r => r.Version).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ResumeDto.From).ToList());
    }

    private static async Task<Ok<ResumeDto>> Get(Guid id, AppDbContext db, CancellationToken ct) =>
        TypedResults.Ok(ResumeDto.From(await db.Resumes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw NotFound()));

    private static async Task<FileStreamHttpResult> Download(Guid id, HttpContext http, AppDbContext db, IFileStore files,
        CancellationToken ct)
    {
        var r = await db.Resumes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound();
        var stream = await files.OpenReadAsync(r.StorageKey, ct) ?? throw NotFound();
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.File(stream, DocxContentType, r.OriginalFilename);
    }

    // ---------- activate / delete ----------

    private static async Task<Ok<ResumeDto>> Activate(Guid id, AppDbContext db, CancellationToken ct)
    {
        var r = await db.Resumes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound();
        if (r.ParseStatus != WorkStatus.Ready)
            throw FieldErrors.Single("id", "Only a version that finished parsing can be made active.");
        if (!r.IsActive)
            await ActivateAsync(db, r, ct);
        return TypedResults.Ok(ResumeDto.From(r));
    }

    /// <summary>Exactly one active résumé per user: deactivate the current one first (partial unique index).</summary>
    internal static async Task ActivateAsync(AppDbContext db, Resume r, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Resumes.Where(x => x.IsActive && x.Id != r.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);
        r.IsActive = true;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static async Task<NoContent> Delete(Guid id, AppDbContext db, IFileStore files, ICurrentUser current,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var r = await db.Resumes.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw NotFound();
        // Files first, then the row; the profile (and later analyses and tailored documents) cascade from it.
        await files.DeletePrefixAsync(Resume.PrefixFor(current.UserId, r.Id), ct);
        db.Resumes.Remove(r);
        await db.SaveChangesAsync(ct);
        LogDeleted(loggers.CreateLogger(typeof(ResumeEndpoints)), r.Id);
        return TypedResults.NoContent();
    }

    // ---------- profile ----------

    private static async Task<Ok<ResumeProfileDto>> GetProfile(Guid id, AppDbContext db, CancellationToken ct)
    {
        var p = await db.ResumeProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.ResumeId == id, ct) ?? throw NotFound();
        return TypedResults.Ok(ToDto(p));
    }

    private static async Task<Ok<ResumeProfileDto>> UpdateProfile(Guid id, UpdateProfileRequest body, AppDbContext db,
        PromptRegistry prompts, TimeProvider clock, CancellationToken ct)
    {
        var p = await db.ResumeProfiles.FirstOrDefaultAsync(x => x.ResumeId == id, ct) ?? throw NotFound();
        var parsed = Deserialize<ParsedProfile>(p.Parsed);
        if (body.UserEdited is { } edits)
        {
            var effectiveJson = JsonSerializer.Serialize(edits.ApplyTo(parsed), ResumeDto.Json);
            var schemaErrors = prompts.Get(AiTaskNames.ResumeParse).Schema.Validate(effectiveJson);
            var domainErrors = ResumeParseTask.Validate(edits.ApplyTo(parsed));
            if (schemaErrors.Count > 0 || domainErrors.Count > 0)
            {
                var errors = new FieldErrors();
                foreach (var e in schemaErrors) errors.Add("userEdited" + (string.IsNullOrEmpty(e.Path) ? "" : "." + e.Path.TrimStart('#', '/', '.')), e.Kind.ToString());
                foreach (var e in domainErrors) errors.Add("userEdited", e);
                errors.ThrowIfAny();
            }
            p.UserEdited = JsonSerializer.Serialize(edits, ResumeDto.Json);
        }
        else
        {
            p.UserEdited = null;
        }
        p.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(p));
    }

    internal static ResumeProfileDto ToDto(ResumeProfile p)
    {
        var parsed = Deserialize<ParsedProfile>(p.Parsed);
        var edits = p.UserEdited is null ? null : Deserialize<ProfileEdits>(p.UserEdited);
        return new ResumeProfileDto(parsed, edits, edits?.ApplyTo(parsed) ?? parsed, p.UpdatedAt);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, ResumeDto.Json) ?? throw new InvalidDataException("Stored profile JSON is empty.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Résumé {Id} uploaded as version {Version} ({SizeBytes} bytes)")]
    private static partial void LogUploaded(ILogger logger, Guid id, int version, int sizeBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload rejected: {Reason} ({SizeBytes} bytes)")]
    private static partial void LogRejected(ILogger logger, string reason, int sizeBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Résumé {Id} deleted")]
    private static partial void LogDeleted(ILogger logger, Guid id);
}
