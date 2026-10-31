using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using FluentAssertions;
using Tailorly.Docx;
using Tailorly.Docx.Tailoring;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Unit.Docx;

/// <summary>
/// Format preservation (PRD §14.1, F8 acceptance "styles/fonts/spacing unchanged outside edited text"): for every fixture,
/// a round trip with no edits, and a rewrite of every editable paragraph at once, must leave every package part except
/// <c>word/document.xml</c> byte for byte unchanged (styles, numbering, theme, settings, fonts, headers, footers,
/// relationships, content types), and the document itself unchanged apart from the text runs of the targeted paragraphs:
/// their paragraph properties (style, numbering, indent, spacing, section breaks), every other paragraph, table and text
/// box, and the body's section properties stay identical. The result must not add OpenXML validation errors.
/// LibreOffice opening the results is checked in <c>Integration.Docx.LibreOfficeOpensTests</c>.
/// </summary>
public sealed class FormatPreservationTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public static TheoryData<string> Fixtures() => new(ResumeFixtures.All.Select(f => f.Name));

    [Fact]
    public void There_are_at_least_ten_diverse_fixtures()
    {
        ResumeFixtures.All.Should().HaveCountGreaterThanOrEqualTo(10);
        var findings = ResumeFixtures.All.Select(f => ResumeExtractor.Extract(new MemoryStream(f.Docx)).Findings).ToList();
        findings.Should().Contain(x => x.HasColumns, "a two-column résumé");
        findings.Should().Contain(x => x.HasTables, "a table layout");
        findings.Should().Contain(x => x.HasTextBoxes, "a text box");
        findings.Should().Contain(x => x.HasImages, "an image");
        findings.Should().Contain(x => x.HeaderFooterText, "header or footer text");
        findings.Should().Contain(x => x.Fonts.Count >= 2, "several fonts");
    }

    /// <summary>
    /// Errors a fixture has on purpose: old documents carry markup the current schema no longer allows, and the applier
    /// must still work on them (only errors it adds count).
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownErrors = new()
    {
        ["unicode-special-characters"] = ["Sch_InvalidElementContentExpectingComplex: The element has invalid child element " +
            "'http://schemas.openxmlformats.org/wordprocessingml/2006/main:smartTag'."],
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_is_valid_and_has_editable_paragraphs(string name)
    {
        var docx = ResumeFixtures.Get(name).Docx;
        DocxSafetyInspector.Inspect(name + ".docx", docx).IsOk.Should().BeTrue("the fixture must pass the upload checks too");
        ValidationErrors(docx).Should().BeEquivalentTo(KnownErrors.GetValueOrDefault(name, []),
            "the fixtures are written to the schema, apart from deliberate legacy markup");
        var m = ResumeExtractor.Extract(new MemoryStream(docx));
        m.Paragraphs.Count(p => p.Flags.Editable).Should().BeGreaterThanOrEqualTo(4);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Applying_no_edits_changes_nothing(string name)
    {
        var original = ResumeFixtures.Get(name).Docx;
        var output = EditApplier.Apply(original, []);

        AssertOtherPartsUnchanged(original, output);
        Canonical(Document(output)).Should().BeEquivalentTo(Canonical(Document(original)),
            o => o.WithStrictOrdering(), "the document XML must be the same apart from serialisation details");
        Shape(Extract(output)).Should().Equal(Shape(Extract(original)));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Rewriting_every_editable_paragraph_changes_only_their_text(string name)
    {
        var original = ResumeFixtures.Get(name).Docx;
        var before = Extract(original);
        var targets = before.Paragraphs.Where(p => p.Flags.Editable).ToList();
        var edits = targets.Select(p => new EditToApply(p.Id, p.Fingerprint, Rewrite(p))).ToList();

        var output = EditApplier.Apply(original, edits);
        var after = Extract(output);

        // 1. Every other part is byte for byte the same.
        AssertOtherPartsUnchanged(original, output);

        // 2. The document is the same once the targeted paragraphs are reduced to their properties.
        var targeted = targets.Select(p => int.Parse(p.Id[1..]) - 1).ToHashSet();
        Reduced(Document(output), targeted).ToString(SaveOptions.DisableFormatting)
            .Should().Be(Reduced(Document(original), targeted).ToString(SaveOptions.DisableFormatting));

        // 3. Targeted paragraphs: new text, prefix runs kept, one run after the prefix with the dominant run's formatting.
        after.Paragraphs.Should().HaveCount(before.Paragraphs.Count);
        foreach (var (p, e) in targets.Zip(edits))
        {
            var q = after.Paragraphs.Single(x => x.Id == p.Id);
            q.Text.Should().Be(e.NewText, p.Id);
            var prefixRuns = PrefixRuns(p);
            q.Runs.Take(prefixRuns.Count).Select(r => (r.Text, r.FormatKey)).Should().Equal(prefixRuns, $"{p.Id} keeps its prefix");
            q.Runs.Should().HaveCount(prefixRuns.Count + 1, $"{p.Id} has one run after the prefix");
            q.Runs[^1].FormatKey.Should().Be(DominantFormat(p), $"{p.Id} keeps the dominant run's formatting");
            q.Flags.Should().Be(p.Flags with { }, $"{p.Id} keeps its flags");
        }

        // 4. Untouched paragraphs extract exactly as before.
        Shape(after).Where(s => !targeted.Contains(int.Parse(s.Id[1..]) - 1))
            .Should().Equal(Shape(before).Where(s => !targeted.Contains(int.Parse(s.Id[1..]) - 1)));

        // 5. No new validation errors (the applier also enforces this).
        ValidationErrors(output).Should().BeEquivalentTo(ValidationErrors(original));
    }

    [Fact]
    public void A_changed_paragraph_is_a_stale_edit_and_a_locked_or_prefix_breaking_edit_is_refused()
    {
        var original = ResumeFixtures.Get("classic-single-column").Docx;
        var m = Extract(original);
        var editable = m.Paragraphs.First(p => p.Flags.Editable && p.Flags.LockedPrefixLength == 0);
        var labelled = m.Paragraphs.First(p => p.Flags.Editable && p.Flags.LockedPrefixLength > 0);
        var locked = m.Paragraphs.First(p => p.Flags.Locked && p.Text.Length > 0);

        FluentActions.Invoking(() => EditApplier.Apply(original, [new EditToApply(editable.Id, "deadbeef", "Changed.")]))
            .Should().Throw<StaleEditException>().Which.ParagraphId.Should().Be(editable.Id);
        FluentActions.Invoking(() => EditApplier.Apply(original, [new EditToApply("p999", "00000000", "Changed.")]))
            .Should().Throw<StaleEditException>();
        FluentActions.Invoking(() => EditApplier.Apply(original, [new EditToApply(locked.Id, locked.Fingerprint, "Changed.")]))
            .Should().Throw<EditRejectedException>();
        FluentActions.Invoking(() => EditApplier.Apply(original, [new EditToApply(labelled.Id, labelled.Fingerprint, "Other label: C#")]))
            .Should().Throw<EditRejectedException>().Which.Reason.Should().Contain("prefix");
        FluentActions.Invoking(() => EditApplier.Apply(original, [new EditToApply(editable.Id, editable.Fingerprint, "Two\nparagraphs")]))
            .Should().Throw<EditRejectedException>();
    }

    [Fact]
    public void The_original_bytes_are_never_modified()
    {
        var original = ResumeFixtures.Get("typed-bullets-bold-leadins").Docx;
        var copy = original.ToArray();
        var m = Extract(original);
        EditApplier.Apply(original, m.Paragraphs.Where(p => p.Flags.Editable).Select(p => new EditToApply(p.Id, p.Fingerprint, Rewrite(p))).ToList());
        original.Should().Equal(copy);
    }

    [Fact]
    public void A_typed_bullet_with_a_bold_label_keeps_both_and_a_glyph_inside_a_run_is_split()
    {
        var original = ResumeFixtures.Get("typed-bullets-bold-leadins").Docx;
        var m = Extract(original);
        var labelled = m.Paragraphs.Single(p => p.Text.StartsWith("• Pipelines:", StringComparison.Ordinal));
        labelled.Flags.LockedPrefixLength.Should().Be("• Pipelines: ".Length);
        var plain = m.Paragraphs.Single(p => p.Text.StartsWith("• Partnered", StringComparison.Ordinal));
        plain.Flags.LockedPrefixLength.Should().Be(2, "the glyph and its space are locked even inside one run");

        var output = EditApplier.Apply(original,
        [
            new EditToApply(labelled.Id, labelled.Fingerprint, "• Pipelines: built Airflow pipelines that load clickstream data into Snowflake hourly."),
            new EditToApply(plain.Id, plain.Fingerprint, "• Worked with analysts to model orders and returns for finance."),
        ]);
        var after = Extract(output);
        var q = after.Paragraphs.Single(p => p.Id == plain.Id);
        q.Runs.Select(r => r.Text).Should().Equal("• ", "Worked with analysts to model orders and returns for finance.");
        after.Paragraphs.Single(p => p.Id == labelled.Id).Runs.Select(r => (r.Text, r.Bold))
            .Should().Equal(("• ", false), ("Pipelines: ", true), ("built Airflow pipelines that load clickstream data into Snowflake hourly.", false));
    }

    [Fact]
    public void A_section_break_inside_an_edited_bullet_is_kept()
    {
        var original = ResumeFixtures.Get("google-docs-sections").Docx;
        var m = Extract(original);
        var p = m.Paragraphs.Single(x => x.Text.StartsWith("Set up offline evaluation", StringComparison.Ordinal));
        p.Flags.Editable.Should().BeTrue();
        var output = EditApplier.Apply(original, [new EditToApply(p.Id, p.Fingerprint, "Set up offline evaluation so each model change ships with a report.")]);

        var index = int.Parse(p.Id[1..]) - 1;
        static XElement SectPr(XDocument d, int i) => BodyParagraphs(d)[i].Element(W + "pPr")!.Element(W + "sectPr")!;
        Canon(SectPr(Document(output), index)).ToString().Should().Be(Canon(SectPr(Document(original), index)).ToString());
    }

    [Fact]
    public void Special_characters_in_new_text_are_escaped_and_round_trip()
    {
        var original = ResumeFixtures.Get("unicode-special-characters").Docx;
        var m = Extract(original);
        var p = m.Paragraphs.First(x => x.Flags.Editable && x.Section == ResumeSections.Experience);
        const string text = "Built <UPI> & “collect” flows – ₹ settlements, co-operative banks, आदित्य.";
        var output = EditApplier.Apply(original, [new EditToApply(p.Id, p.Fingerprint, p.Text[..p.Flags.LockedPrefixLength] + text)]);
        Extract(output).Paragraphs.Single(x => x.Id == p.Id).Text.Should().EndWith(text);
    }

    /// <summary>
    /// Opt-in: the same checks on the owner's own résumés (<c>TAILORLY_LOCAL_RESUMES</c>), never committed. Reports counts only.
    /// </summary>
    [Fact]
    public void Real_resumes_in_a_local_folder_keep_their_format()
    {
        var dir = Environment.GetEnvironmentVariable("TAILORLY_LOCAL_RESUMES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Assert.Skip("Set TAILORLY_LOCAL_RESUMES to run against local résumé files.");
            return;
        }
        var files = Directory.GetFiles(dir, "*.docx");
        files.Should().NotBeEmpty();
        for (var n = 0; n < files.Length; n++)
        {
            var original = File.ReadAllBytes(files[n]);
            var before = Extract(original);
            var targets = before.Paragraphs.Where(p => p.Flags.Editable).ToList();
            var output = EditApplier.Apply(original, targets.Select(p => new EditToApply(p.Id, p.Fingerprint, Rewrite(p))).ToList());
            AssertOtherPartsUnchanged(original, output);
            var targeted = targets.Select(p => int.Parse(p.Id[1..]) - 1).ToHashSet();
            Reduced(Document(output), targeted).ToString(SaveOptions.DisableFormatting)
                .Should().Be(Reduced(Document(original), targeted).ToString(SaveOptions.DisableFormatting), $"file #{n + 1}");
            var after = Extract(output);
            foreach (var p in targets)
                after.Paragraphs.Single(x => x.Id == p.Id).Text.Should().Be(Rewrite(p), $"file #{n + 1} {p.Id}");
            var summary = $"local résumé #{n + 1}: {before.Paragraphs.Count} paragraphs, {targets.Count} rewritten, " +
                          $"{targets.Count(EditValidator.FormattingSimplified)} with mixed inline formatting, " +
                          $"validation errors before/after {ValidationErrors(original).Count}/{ValidationErrors(output).Count}";
            TestContext.Current.SendDiagnosticMessage(summary);
            if (Environment.GetEnvironmentVariable("TAILORLY_LOCAL_RESUMES_REPORT") is { Length: > 0 } report)
                File.AppendAllText(report, summary + Environment.NewLine);
        }
    }

    // ---------- helpers ----------

    /// <summary>A deterministic rewrite: prefix kept, the rest changed, with characters that need escaping.</summary>
    internal static string Rewrite(DocParagraph p)
    {
        var prefix = p.Text[..p.Flags.LockedPrefixLength];
        var rest = p.Text[p.Flags.LockedPrefixLength..];
        var lead = rest[..(rest.Length - rest.TrimStart().Length)];
        var body = rest.Trim();
        var changed = body.Contains(", ", StringComparison.Ordinal)
            ? string.Join(", ", body.Split(", ").Reverse())
            : "Rewritten – " + body;
        return prefix + lead + changed + " & <kept>";
    }

    private static DocModel Extract(byte[] docx) => ResumeExtractor.Extract(new MemoryStream(docx));

    private static List<(string Id, string Fingerprint, string Section, string Text, ParagraphFlags Flags, string Runs)> Shape(DocModel m) =>
        m.Paragraphs.Select(p => (p.Id, p.Fingerprint, p.Section, p.Text, p.Flags,
            string.Join("|", p.Runs.Select(r => r.Text + "§" + r.FormatKey)))).ToList();

    /// <summary>The runs inside the locked prefix as (text, format), the straddling run cut at the prefix end.</summary>
    private static List<(string, string)> PrefixRuns(DocParagraph p)
    {
        var list = new List<(string, string)>();
        var pos = 0;
        foreach (var r in p.Runs)
        {
            if (pos >= p.Flags.LockedPrefixLength) break;
            var take = Math.Min(r.Text.Length, p.Flags.LockedPrefixLength - pos);
            list.Add((r.Text[..take], r.FormatKey));
            pos += r.Text.Length;
        }
        return list;
    }

    /// <summary>The format of the run with the most text after the prefix (the first on a tie), as the applier chooses it.</summary>
    private static string DominantFormat(DocParagraph p)
    {
        var pos = 0;
        var best = (Length: -1, Key: "");
        foreach (var r in p.Runs)
        {
            var start = pos;
            pos += r.Text.Length;
            if (pos <= p.Flags.LockedPrefixLength) continue;
            var length = start < p.Flags.LockedPrefixLength ? pos - p.Flags.LockedPrefixLength : r.Text.Length;
            if (length > best.Length) best = (length, r.FormatKey);
        }
        return best.Key;
    }

    private static void AssertOtherPartsUnchanged(byte[] original, byte[] output)
    {
        using var a = new ZipArchive(new MemoryStream(original), ZipArchiveMode.Read);
        using var b = new ZipArchive(new MemoryStream(output), ZipArchiveMode.Read);
        b.Entries.Select(e => e.FullName).Should().Equal(a.Entries.Select(e => e.FullName), "same parts, same order");
        foreach (var e in a.Entries.Where(e => e.FullName != "word/document.xml"))
            Bytes(b.GetEntry(e.FullName)!).Should().Equal(Bytes(e), $"{e.FullName} must be unchanged byte for byte");
    }

    private static byte[] Bytes(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static XDocument Document(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        using var s = zip.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(s, LoadOptions.PreserveWhitespace);
    }

    /// <summary>Body paragraphs in the extractor's order (VML fallback copies skipped).</summary>
    private static List<XElement> BodyParagraphs(XDocument d) =>
        d.Root!.Element(W + "body")!.Descendants(W + "p").Where(p => !p.Ancestors(Mc + "Fallback").Any()).ToList();

    /// <summary>The canonical document with the targeted paragraphs reduced to their properties.</summary>
    private static XElement Reduced(XDocument d, IReadOnlySet<int> targeted)
    {
        var copy = new XDocument(d);
        var paragraphs = BodyParagraphs(copy);
        foreach (var i in targeted)
            foreach (var n in paragraphs[i].Nodes().Where(n => n is not XElement { Name.LocalName: "pPr" }).ToList()) n.Remove();
        return Canon(copy.Root!);
    }

    private static List<string> Canonical(XDocument d) => BodyParagraphs(d).Select(p => Canon(p).ToString(SaveOptions.DisableFormatting))
        .Append(Canon(d.Root!).ToString(SaveOptions.DisableFormatting)).ToList();

    /// <summary>Attributes sorted, namespace declarations dropped, empty text dropped: serialisation details do not count.</summary>
    private static XElement Canon(XElement e) => new(e.Name,
        e.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal)
            .ThenBy(a => a.Name.LocalName, StringComparer.Ordinal).Select(a => new XAttribute(a.Name, a.Value)),
        e.Nodes().Select<XNode, XNode?>(n => n switch
        {
            XElement c => Canon(c),
            XText t when t.Value.Length > 0 => new XText(t.Value),
            _ => null,
        }).Where(n => n is not null));

    internal static List<string> ValidationErrors(byte[] docx)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        return new OpenXmlValidator(FileFormatVersions.Microsoft365).Validate(doc.MainDocumentPart!)
            .Select(e => $"{e.Id}: {e.Description}").ToList();
    }
}
