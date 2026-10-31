using DocumentFormat.OpenXml.Packaging;
using FluentAssertions;
using Tailorly.Docx;
using Tailorly.Docx.Tailoring;

namespace Tailorly.Tests.Unit.Docx;

/// <summary>The word diff (SDD §10.4) and the cover letter writer (SDD §10.6).</summary>
public sealed class DiffAndCoverLetterTests
{
    [Theory]
    [InlineData("Built REST APIs for the payments team.", "Built REST APIs in .NET and PostgreSQL for the payments team.")]
    [InlineData("Languages: C#, SQL", "Languages: SQL, C#, TypeScript")]
    [InlineData("Same text.", "Same text.")]
    [InlineData("", "All new.")]
    [InlineData("Removed entirely.", "")]
    public void The_diff_rebuilds_both_sides(string original, string proposed)
    {
        var diff = DiffBuilder.Words(original, proposed);
        string.Concat(diff.Where(d => d.Type != DiffKinds.Insert).Select(d => d.Text)).Should().Be(original);
        string.Concat(diff.Where(d => d.Type != DiffKinds.Delete).Select(d => d.Text)).Should().Be(proposed);
        diff.Zip(diff.Skip(1)).All(p => p.First.Type != p.Second.Type).Should().BeTrue("adjacent pieces of one kind are merged");
    }

    [Fact]
    public void The_diff_marks_inserted_words()
    {
        var diff = DiffBuilder.Words("Built REST APIs for the payments team.", "Built REST APIs in .NET and PostgreSQL for the payments team.");
        diff.Where(d => d.Type == DiffKinds.Insert).Select(d => d.Text.Trim()).Should().ContainSingle()
            .Which.Should().Be("in .NET and PostgreSQL");
        diff.Should().NotContain(d => d.Type == DiffKinds.Delete);
    }

    [Fact]
    public void The_cover_letter_has_one_plain_paragraph_per_entry_and_validates()
    {
        var paragraphs = new[] { "Hello Acme Cloud team,", "I build .NET & PostgreSQL services <for payments>.", "Best regards,\tAsha" };
        var bytes = CoverLetterWriter.Write(paragraphs);

        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        doc.MainDocumentPart!.Document!.Body!.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
            .Select(p => p.InnerText).Should().Equal("Hello Acme Cloud team,", "I build .NET & PostgreSQL services <for payments>.", "Best regards, Asha");
        doc.MainDocumentPart.StyleDefinitionsPart.Should().NotBeNull();
        FormatPreservationTests.ValidationErrors(bytes).Should().BeEmpty();
        DocxSafetyInspector.Inspect("letter.docx", bytes).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Characters_not_allowed_in_XML_are_dropped()
    {
        var bytes = CoverLetterWriter.Write(["Bad\u0001 char\u000B here."]);
        using var doc = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        doc.MainDocumentPart!.Document!.Body!.InnerText.Should().Be("Bad char here.");
    }
}
