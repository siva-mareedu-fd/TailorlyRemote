using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tailorly.Docx.Tailoring;

/// <summary>An accepted edit to apply: the paragraph, the fingerprint it was proposed against, the whole new paragraph text.</summary>
public sealed record EditToApply(string ParagraphId, string Fingerprint, string NewText);

/// <summary>The résumé no longer matches the proposal: a paragraph's text changed (409 <c>STALE_EDIT</c>).</summary>
public sealed class StaleEditException(string paragraphId)
    : Exception($"Paragraph {paragraphId} changed since the edit was proposed.")
{
    public string ParagraphId { get; } = paragraphId;
}

/// <summary>An edit that cannot be applied safely (not editable, does not keep the locked prefix, has a line break).</summary>
public sealed class EditRejectedException(string paragraphId, string reason)
    : Exception($"Edit to paragraph {paragraphId} rejected: {reason}.")
{
    public string ParagraphId { get; } = paragraphId;
    public string Reason { get; } = reason;
}

/// <summary>The edited document failed the OpenXML validator where the original did not (SDD §10.2 post-condition).</summary>
public sealed class DocxValidationException(IReadOnlyList<string> paragraphIds, int newErrors)
    : Exception($"The edited document has {newErrors} new validation error(s).")
{
    /// <summary>Paragraphs the new errors point into (ids only, never text).</summary>
    public IReadOnlyList<string> ParagraphIds { get; } = paragraphIds;
}

/// <summary>
/// Applies accepted edits to a <b>copy</b> of the original .docx (SDD §10.2, ADR-003). For each paragraph:
/// <list type="number">
/// <item>the fingerprint must still match (else <see cref="StaleEditException"/>);</item>
/// <item>the runs covering the locked prefix (a bold "Languages:" label, a typed bullet) are kept untouched, a run that
/// straddles the prefix end is split;</item>
/// <item>the new text goes into the <b>dominant</b> remaining run (the most text), keeping its run properties; the other
/// remaining text runs are removed; paragraph properties (style, numbering, indent, spacing, a section break) are never
/// touched.</item>
/// </list>
/// Only <c>word/document.xml</c> is rewritten: every other part (styles, numbering, theme, settings, headers, footers,
/// fonts, custom XML, relationships) is copied byte for byte, so they cannot change. The original bytes are never
/// mutated. The result is checked with <see cref="OpenXmlValidator"/>; an edit set that adds validation errors is rejected.
/// </summary>
public static class EditApplier
{
    public static byte[] Apply(byte[] original, IReadOnlyList<EditToApply> edits)
    {
        string documentXml, mainEntry;
        using (var input = new MemoryStream(original, writable: false))
        using (var doc = WordprocessingDocument.Open(input, isEditable: false, new OpenSettings { AutoSave = false }))
        {
            var main = doc.MainDocumentPart ?? throw new InvalidDataException("The document has no main part.");
            var model = ResumeExtractor.Extract(doc);
            var byId = model.Paragraphs.ToDictionary(p => p.Id, StringComparer.Ordinal);
            var elements = ResumeExtractor.BodyParagraphs(main.Document!.Body!).ToList();

            foreach (var e in edits)
            {
                if (!byId.TryGetValue(e.ParagraphId, out var info)) throw new StaleEditException(e.ParagraphId);
                if (info.Fingerprint != e.Fingerprint) throw new StaleEditException(e.ParagraphId);
                if (!info.Flags.Editable) throw new EditRejectedException(e.ParagraphId, "the paragraph is locked");
                var prefix = info.Text[..info.Flags.LockedPrefixLength];
                if (!e.NewText.StartsWith(prefix, StringComparison.Ordinal))
                    throw new EditRejectedException(e.ParagraphId, "the locked prefix changed");
                if (e.NewText.Contains('\n') || e.NewText.Contains('\r'))
                    throw new EditRejectedException(e.ParagraphId, "a paragraph must stay one paragraph");
                var index = int.Parse(e.ParagraphId.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture) - 1;
                ReplaceText(elements[index], info, e.NewText[prefix.Length..]);
            }

            documentXml = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""" + "\r\n" + main.Document.OuterXml;
            mainEntry = main.Uri.OriginalString.TrimStart('/');
        }

        var output = Repack(original, mainEntry, documentXml);
        Validate(original, output);
        return output;
    }

    /// <summary>Puts <paramref name="newEditable"/> after the prefix runs, in the dominant run; removes the other text runs.</summary>
    private static void ReplaceText(Paragraph p, DocParagraph info, string newEditable)
    {
        var prefixLength = info.Flags.LockedPrefixLength;
        var editable = new List<Run>();
        var pos = 0;
        foreach (var (run, text) in ResumeExtractor.TextRuns(p).ToList())
        {
            var start = pos;
            pos += text.Length;
            if (pos <= prefixLength) continue;
            editable.Add(start < prefixLength ? SplitRun(run, prefixLength - start) : run);
        }
        if (editable.Count == 0) throw new EditRejectedException(info.Id, "nothing after the locked prefix");

        // Dominant run: the most text (the first one on a tie); its run properties carry the new text.
        var dominant = editable.OrderByDescending(r => ResumeExtractor.RunText(r).Length).First();
        foreach (var child in dominant.ChildElements.Where(c => c is not RunProperties).ToList()) child.Remove();
        dominant.AppendChild(new Text(newEditable) { Space = SpaceProcessingModeValues.Preserve });
        foreach (var r in editable.Where(r => !ReferenceEquals(r, dominant))) r.Remove();

        // Spelling marks described the old text; Word recomputes them.
        foreach (var mark in p.Descendants<ProofError>().ToList()) mark.Remove();
    }

    /// <summary>
    /// Splits <paramref name="run"/> after <paramref name="offset"/> characters of its text: the run keeps the head, a new
    /// run with the same attributes and properties, inserted right after it, gets the tail. Returns the tail run.
    /// </summary>
    internal static Run SplitRun(Run run, int offset)
    {
        var tail = (Run)run.CloneNode(false);
        if (run.RunProperties is { } rpr) tail.AppendChild(rpr.CloneNode(true));
        var pos = 0;
        foreach (var child in run.ChildElements.Where(c => c is not RunProperties).ToList())
        {
            var text = ResumeExtractor.ChildText(child);
            if (pos >= offset)
            {
                child.Remove();
                tail.AppendChild(child);
            }
            else if (pos + text.Length > offset && child is Text t)
            {
                var cut = offset - pos;
                tail.AppendChild(new Text(t.Text[cut..]) { Space = SpaceProcessingModeValues.Preserve });
                t.Text = t.Text[..cut];
                t.Space = SpaceProcessingModeValues.Preserve;
            }
            pos += text.Length;
        }
        run.InsertAfterSelf(tail);
        return tail;
    }

    /// <summary>A new package: every zip entry copied as is, except the main document part, which gets <paramref name="xml"/>.</summary>
    private static byte[] Repack(byte[] original, string mainEntry, string xml)
    {
        using var input = new MemoryStream(original, writable: false);
        using var zipIn = new ZipArchive(input, ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var zipOut = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var replaced = false;
            foreach (var entry in zipIn.Entries)
            {
                var copy = zipOut.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = entry.LastWriteTime;
                using var target = copy.Open();
                if (string.Equals(entry.FullName.TrimStart('/'), mainEntry, StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(xml);
                    target.Write(bytes);
                    replaced = true;
                }
                else
                {
                    using var source = entry.Open();
                    source.CopyTo(target);
                }
            }
            if (!replaced) throw new InvalidDataException("The main document part was not found in the package.");
        }
        return output.ToArray();
    }

    /// <summary>
    /// Post-condition (SDD §10.2): the main document part may not have validation errors the original did not have. Many
    /// real résumés already have some (written by other editors); only new ones count.
    /// </summary>
    private static void Validate(byte[] original, byte[] output)
    {
        var before = Errors(original).Select(e => e.Key).ToList();
        var after = Errors(output);
        var remaining = before.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        var added = new List<(string Key, string? ParagraphId)>();
        foreach (var e in after)
        {
            if (remaining.TryGetValue(e.Key, out var n) && n > 0) remaining[e.Key] = n - 1;
            else added.Add(e);
        }
        if (added.Count > 0)
            throw new DocxValidationException(added.Select(a => a.ParagraphId ?? "?").Distinct().ToList(), added.Count);
    }

    private static List<(string Key, string? ParagraphId)> Errors(byte[] docx)
    {
        using var ms = new MemoryStream(docx, writable: false);
        using var doc = WordprocessingDocument.Open(ms, isEditable: false, new OpenSettings { AutoSave = false });
        var main = doc.MainDocumentPart!;
        var paragraphs = new Dictionary<Paragraph, string>(ReferenceEqualityComparer.Instance);
        foreach (var (p, i) in ResumeExtractor.BodyParagraphs(main.Document!.Body!).Select((p, i) => (p, i)))
            paragraphs[p] = "p" + (i + 1);
        var validator = new OpenXmlValidator(FileFormatVersions.Microsoft365);
        return validator.Validate(main)
            .Select(e =>
            {
                var paragraph = e.Node is null ? null : e.Node as Paragraph ?? e.Node.Ancestors<Paragraph>().FirstOrDefault();
                return ($"{e.Id}|{e.Description}", paragraph is not null && paragraphs.TryGetValue(paragraph, out var id) ? id : null);
            })
            .ToList();
    }
}
