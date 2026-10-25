using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using FluentAssertions;
using Tailorly.Docx;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Unit;

/// <summary>Extraction (SDD §10.1) and layout analysis on synthetic fixtures.</summary>
public sealed class ResumeExtractorTests
{
    private static DocModel Extract(byte[] docx) => ResumeExtractor.Extract(new MemoryStream(docx));

    private static DocParagraph P(DocModel m, string startsWith) =>
        m.Paragraphs.Single(p => p.Text.StartsWith(startsWith, StringComparison.Ordinal));

    [Fact]
    public void Paragraph_ids_are_ordinal_and_fingerprints_are_8_hex_of_the_normalised_text()
    {
        var m = Extract(DocxBuilder.SampleResume());
        m.Paragraphs.Select(p => p.Id).Should().Equal(Enumerable.Range(1, m.Paragraphs.Count).Select(i => "p" + i));
        m.Paragraphs.Should().OnlyContain(p => p.Fingerprint.Length == 8 && p.Fingerprint.All(Uri.IsHexDigit));

        // Same text (whitespace aside) → same fingerprint; a change → a different one.
        ResumeExtractor.Fingerprint(ResumeExtractor.Normalize("Built  REST APIs ")).Should()
            .Be(ResumeExtractor.Fingerprint(ResumeExtractor.Normalize("Built REST APIs")));
        ResumeExtractor.Fingerprint("a").Should().NotBe(ResumeExtractor.Fingerprint("b"));
    }

    [Fact]
    public void Extraction_is_deterministic()
    {
        var bytes = DocxBuilder.SampleResume();
        Extract(bytes).Paragraphs.Select(p => (p.Id, p.Fingerprint, p.Section, p.Flags))
            .Should().Equal(Extract(bytes).Paragraphs.Select(p => (p.Id, p.Fingerprint, p.Section, p.Flags)));
    }

    [Fact]
    public void Sections_follow_headings_and_the_contact_block_comes_first()
    {
        var m = Extract(DocxBuilder.SampleResume());
        P(m, "Asha Verma").Section.Should().Be(ResumeSections.Contact);
        P(m, "Hyderabad").Section.Should().Be(ResumeSections.Contact);
        P(m, "Backend engineer").Section.Should().Be(ResumeSections.Summary);
        P(m, "Built REST APIs").Section.Should().Be(ResumeSections.Experience);
        P(m, "Languages:").Section.Should().Be(ResumeSections.Skills);
        P(m, "B.Tech").Section.Should().Be(ResumeSections.Education);
        P(m, "Microsoft Certified").Section.Should().Be(ResumeSections.Certifications);
    }

    [Fact]
    public void Lock_rules_protect_contact_headings_titles_dates_education_and_certifications()
    {
        var m = Extract(DocxBuilder.SampleResume());
        P(m, "Asha Verma").Flags.LockReason.Should().Be("contact");
        P(m, "Summary").Flags.LockReason.Should().Be("heading");
        P(m, "Senior Software Engineer").Flags.LockReason.Should().Be("title_line");
        P(m, "Jan 2021").Flags.LockReason.Should().Be("date_line");
        P(m, "B.Tech").Flags.LockReason.Should().Be("education");
        P(m, "Microsoft Certified").Flags.LockReason.Should().Be("certification");
        m.Paragraphs.Where(p => p.Flags.IsHeading).Should().OnlyContain(p => p.Flags.Locked);
    }

    [Fact]
    public void Summary_bullets_and_skills_lines_are_editable_with_a_locked_bold_label()
    {
        var m = Extract(DocxBuilder.SampleResume());
        P(m, "Backend engineer").Flags.Editable.Should().BeTrue();

        var bullet = P(m, "Built REST APIs");
        bullet.Flags.Editable.Should().BeTrue();
        bullet.Flags.IsBullet.Should().BeTrue();
        bullet.ListLevel.Should().Be(0);

        var skills = P(m, "Languages:");
        skills.Flags.Editable.Should().BeTrue();
        skills.Flags.LockedPrefixLength.Should().Be("Languages: ".Length);
        skills.Runs[0].Bold.Should().BeTrue();
    }

    [Fact]
    public void Plain_skills_label_without_bold_still_gets_a_prefix()
    {
        var m = Extract(new DocxBuilder().Title("N").Heading("Technical Skills").Para("Cloud: AWS, Azure").Build());
        P(m, "Cloud:").Flags.LockedPrefixLength.Should().Be("Cloud:".Length);
    }

    [Fact]
    public void Complex_paragraphs_are_locked_hyperlinks_tabs_images_and_tracked_changes()
    {
        var m = Extract(new DocxBuilder()
            .Title("N")
            .Heading("Experience")
            .Hyperlink("Portfolio site with my projects and talks", "https://example.test")
            .WithTab("Payments platform rebuild for the whole bank", "2020")
            .TrackedInsertion("Led the migration of eleven services to containers in production.")
            .Bullet("Plain bullet that can be edited by the tailoring step later on.")
            .Build());

        P(m, "Portfolio").Flags.Should().Match<ParagraphFlags>(f => f.Complex && f.Locked && f.LockReason == "complex");
        P(m, "Payments platform").Flags.Complex.Should().BeTrue();
        P(m, "Led the migration").Flags.Complex.Should().BeTrue();
        P(m, "Plain bullet").Flags.Editable.Should().BeTrue();
    }

    [Fact]
    public void Text_box_text_is_extracted_once_and_locked()
    {
        var m = Extract(new DocxBuilder().TextBox("asha@example.test").Heading("Summary").Para("Engineer.").Build());
        var boxed = m.Paragraphs.Where(p => p.Text == "asha@example.test").ToList();
        boxed.Should().ContainSingle("the VML fallback copy must not be extracted twice");
        boxed[0].Flags.InTextBox.Should().BeTrue();
        boxed[0].Flags.Locked.Should().BeTrue();
        m.Findings.HasTextBoxes.Should().BeTrue();
    }

    [Fact]
    public void Table_cells_are_marked_in_table()
    {
        var m = Extract(new DocxBuilder().Title("N").Heading("Skills").TableRow("C#, .NET and PostgreSQL", "Docker").Build());
        P(m, "C#, .NET").Flags.InTable.Should().BeTrue();
        m.Findings.HasTables.Should().BeTrue();
    }

    [Fact]
    public void Layout_findings_cover_columns_images_headers_fonts_and_tracked_changes()
    {
        var plain = Extract(DocxBuilder.SampleResume()).Findings;
        plain.Should().Match<FormatFindings>(f => !f.HasColumns && !f.HasImages && !f.HasTables && !f.HasTextBoxes &&
                                                   !f.TrackedChanges && !f.HeaderFooterText);
        plain.WordCount.Should().BeGreaterThan(50);
        plain.EditableParagraphs.Should().BeGreaterThan(0);
        plain.LockedParagraphs.Should().BeGreaterThan(0);
        plain.Fonts.Should().Contain("Calibri");

        var busy = Extract(new DocxBuilder()
            .Columns(2).Image().Header("asha@example.test · +91 90000 00000")
            .Para("Text in Garamond", font: "Garamond").TrackedInsertion("inserted")
            .Build()).Findings;
        busy.HasColumns.Should().BeTrue();
        busy.HasImages.Should().BeTrue();
        busy.HeaderFooterText.Should().BeTrue();
        busy.HeaderFooterContact.Should().BeTrue();
        busy.TrackedChanges.Should().BeTrue();
        busy.Fonts.Should().Contain("Garamond");
    }

    [Fact]
    public void Header_lines_are_read_for_analysis_and_included_in_the_ai_text()
    {
        var m = Extract(new DocxBuilder().Header("asha@example.test").Heading("Summary").Para("Engineer.").Build());
        m.HeaderFooterLines.Should().Equal("asha@example.test");
        ResumeText.Build(m).Should().StartWith("[header/footer] asha@example.test").And.Contain("## Summary");
    }

    /// <summary>PRD F2: extract → apply zero edits → save leaves document.xml and the styles part unchanged.</summary>
    [Fact]
    public void Round_trip_without_edits_keeps_document_and_styles_unchanged()
    {
        var original = DocxBuilder.SampleResume();
        _ = Extract(original);

        using var copy = new MemoryStream();
        copy.Write(original);
        using (var doc = WordprocessingDocument.Open(copy, isEditable: true))
            doc.Save();

        static (XDocument Doc, XDocument Styles) Parts(byte[] bytes)
        {
            using var d = WordprocessingDocument.Open(new MemoryStream(bytes), false);
            using var a = d.MainDocumentPart!.GetStream();
            using var b = d.MainDocumentPart.StyleDefinitionsPart!.GetStream();
            return (XDocument.Load(a), XDocument.Load(b));
        }

        var (doc0, styles0) = Parts(original);
        var (doc1, styles1) = Parts(copy.ToArray());
        XNode.DeepEquals(doc0, doc1).Should().BeTrue();
        XNode.DeepEquals(styles0, styles1).Should().BeTrue();
    }

    /// <summary>
    /// Optional local check against real résumés that are never committed: set TAILORLY_LOCAL_RESUMES to a folder of
    /// .docx files. Only counts are asserted; no text is printed.
    /// </summary>
    [Fact]
    public void Real_resumes_in_a_local_folder_extract_without_errors()
    {
        var dir = Environment.GetEnvironmentVariable("TAILORLY_LOCAL_RESUMES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Assert.Skip("Set TAILORLY_LOCAL_RESUMES to run against local résumé files.");
            return;
        }
        foreach (var file in Directory.GetFiles(dir, "*.docx"))
        {
            var bytes = File.ReadAllBytes(file);
            DocxSafetyInspector.Inspect(Path.GetFileName(file), bytes).IsOk.Should().BeTrue(Path.GetFileName(file));
            var m = Extract(bytes);
            m.Paragraphs.Should().NotBeEmpty();
            m.Findings.WordCount.Should().BeGreaterThan(50);
            var summary =
                $"{Path.GetFileName(file)}: {m.Paragraphs.Count} paragraphs, {m.Findings.EditableParagraphs} editable, " +
                $"{m.Findings.LockedParagraphs} locked, sections {string.Join(",", m.Paragraphs.Select(p => p.Section).Distinct())}, " +
                $"lock reasons {string.Join(",", m.Paragraphs.GroupBy(p => p.Flags.LockReason ?? "editable").Select(g => $"{g.Key}={g.Count()}"))}, " +
                $"tables {m.Findings.HasTables}, columns {m.Findings.HasColumns}, text boxes {m.Findings.HasTextBoxes}";
            TestContext.Current.SendDiagnosticMessage(summary);
            if (Environment.GetEnvironmentVariable("TAILORLY_LOCAL_RESUMES_REPORT") is { Length: > 0 } report)
                File.AppendAllText(report, summary + Environment.NewLine);
        }
    }
}
