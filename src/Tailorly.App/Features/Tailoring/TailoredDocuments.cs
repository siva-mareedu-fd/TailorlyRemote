using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Features.Resumes;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Docx;
using Tailorly.Docx.Tailoring;

namespace Tailorly.App.Features.Tailoring;

/// <summary>Builds <see cref="TailoredDocumentDto"/>s: word diffs computed on read, "before" from the analysis, formats from the PDF flag.</summary>
public sealed class TailoredDocuments(AppDbContext db)
{
    public const string Docx = "docx";
    public const string Pdf = "pdf";

    public Task<bool> PdfEnabledAsync(CancellationToken ct) => FeatureFlags.IsOnAsync(db, FeatureFlag.PdfExportEnabled, ct);

    public async Task<TailoredDocumentDto> ToDtoAsync(TailoredDocument d, CancellationToken ct) => (await ToDtosAsync([d], ct))[0];

    public async Task<List<TailoredDocumentDto>> ToDtosAsync(IReadOnlyList<TailoredDocument> docs, CancellationToken ct)
    {
        var jobIds = docs.Select(d => d.UserJobId).Distinct().ToList();
        var before = await db.AtsAnalyses.AsNoTracking().Where(a => jobIds.Contains(a.UserJobId) && a.Status == WorkStatus.Ready)
            .Select(a => new { a.UserJobId, a.ResumeId, a.AtsScore }).ToListAsync(ct);
        var pdf = docs.Count > 0 && await PdfEnabledAsync(ct);
        return docs.Select(d => ToDto(d, before.FirstOrDefault(b => b.UserJobId == d.UserJobId && b.ResumeId == d.ResumeId)?.AtsScore, pdf))
            .ToList();
    }

    private static TailoredDocumentDto ToDto(TailoredDocument d, short? atsBefore, bool pdf)
    {
        var edits = TailoringJson.Read<StoredEdits>(d.Edits);
        var letter = TailoringJson.Read<StoredLetter>(d.Content);
        var score = TailoringJson.Read<StoredScore>(d.AtsBreakdown);
        var downloadable = d.Status == TailoredStatus.Ready && d.StorageKey is not null;
        return new TailoredDocumentDto(d.Id, d.UserJobId, d.Kind, d.Status, d.ErrorCode, d.SourceResumeVersion,
            d.Kind == DocumentKinds.Resume ? atsBefore : null, d.AtsAfter, score?.Components, score?.Basis,
            edits?.Items.Select(ToDto).ToList() ?? [],
            edits?.UnaddressedGaps ?? [],
            d.Tone,
            letter is not null ? new CoverLetterContentDto(letter.Paragraphs, letter.WordCount, letter.Notes) : null,
            downloadable,
            downloadable ? pdf ? [Docx, Pdf] : [Docx] : [],
            d.CreatedAt, d.UpdatedAt, d.FinalizedAt);
    }

    private static TailoredEditDto ToDto(StoredEdit e)
    {
        var proposed = ResumeExtractor.Normalize(e.NewText);
        return new TailoredEditDto(e.Id, e.ParagraphId, e.Section, e.Original, proposed, e.Rationale,
            DiffBuilder.Words(e.Original, proposed).Select(s => new DiffSegmentDto(s.Type, s.Text)).ToList(),
            e.Flags, e.NewTerms, e.Decision);
    }

    /// <summary><c>Resume - {Company} - {Title}.docx</c> (ApiDesign §7), unsafe characters removed.</summary>
    public static string FileName(TailoredDocument d, UserJob uj, string format)
    {
        var kind = d.Kind == DocumentKinds.CoverLetter ? "Cover letter" : "Resume";
        var name = ResumeEndpoints.SafeFileName($"{kind} - {uj.CompanyName} - {uj.Title}".Replace(':', ' ').Replace('|', ' ').Replace('*', ' ')
            .Replace('?', ' ').Replace('<', ' ').Replace('>', ' '));
        return (name.Length > 150 ? name[..150].TrimEnd() : name) + "." + format;
    }
}

/// <summary>
/// .docx → PDF through Gotenberg's LibreOffice route (SDD §10.6), only when <c>pdf_export.enabled</c> is on and the Compose
/// <c>pdf</c> profile runs it. The document goes over the internal network only.
/// </summary>
public sealed class PdfExport(HttpClient http)
{
    public async Task<byte[]> ConvertAsync(byte[] docx, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(docx);
        file.Headers.ContentType = new MediaTypeHeaderValue(ResumeEndpoints.DocxContentType);
        form.Add(file, "files", "document.docx");
        using var res = await http.PostAsync("forms/libreoffice/convert", form, ct);
        res.EnsureSuccessStatusCode();
        var pdf = await res.Content.ReadAsByteArrayAsync(ct);
        if (pdf.Length < 5 || pdf[0] != '%' || pdf[1] != 'P' || pdf[2] != 'D' || pdf[3] != 'F')
            throw new HttpRequestException("The converter did not return a PDF.");
        return pdf;
    }
}
