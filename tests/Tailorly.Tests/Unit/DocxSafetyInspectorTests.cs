using FluentAssertions;
using Tailorly.Docx;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Unit;

/// <summary>Upload safety (SDD §10.5): every rejection reason, on synthetic files.</summary>
public sealed class DocxSafetyInspectorTests
{
    [Fact]
    public void Accepts_a_plain_resume_with_hyperlinks_tables_images_and_headers()
    {
        var docx = new DocxBuilder()
            .Title("Name").Hyperlink("linkedin.com/in/me", "https://linkedin.com/in/me")
            .TableRow("a", "b").Image().Header("me@example.test").Heading("Experience").Bullet("Did things.")
            .Build();
        DocxSafetyInspector.Inspect("resume.docx", docx).Reason.Should().Be("ok");
        DocxSafetyInspector.Inspect("resume.docx", DocxBuilder.SampleResume()).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Pdf_gets_the_friendly_word_version_message_whatever_its_name()
    {
        foreach (var name in new[] { "resume.pdf", "resume.docx" })
        {
            var r = DocxSafetyInspector.Inspect(name, UnsafeFiles.Pdf());
            r.Verdict.Should().Be(DocxVerdict.UnsupportedType);
            r.Reason.Should().Be("pdf");
            r.Message.Should().Be("Please upload the Word (.docx) version of your résumé.");
        }
    }

    [Theory]
    [InlineData("resume.doc")]
    [InlineData("resume.txt")]
    [InlineData("resume")]
    public void Other_extensions_are_unsupported(string name) =>
        DocxSafetyInspector.Inspect(name, DocxBuilder.SampleResume()).Verdict.Should().Be(DocxVerdict.UnsupportedType);

    [Fact]
    public void Docm_extension_is_unsafe() =>
        DocxSafetyInspector.Inspect("resume.docm", DocxBuilder.SampleResume()).Reason.Should().Be("macros");

    public static TheoryData<string, string> UnsafeCases => new()
    {
        { nameof(UnsafeFiles.WithMacroPart), "macros" },
        { nameof(UnsafeFiles.WithMacroContentType), "macros" },
        { nameof(UnsafeFiles.WithOleObject), "embedded_object" },
        { nameof(UnsafeFiles.WithNestedArchive), "nested_archive" },
        { nameof(UnsafeFiles.WithExternalTemplate), "external_relationship" },
        { nameof(UnsafeFiles.ZipBomb), "zip_bomb" },
        { nameof(UnsafeFiles.TooManyEntries), "too_many_entries" },
        { nameof(UnsafeFiles.WithDoctype), "bad_xml" },
        { nameof(UnsafeFiles.PasswordProtected), "password_protected" },
    };

    [Theory]
    [MemberData(nameof(UnsafeCases))]
    public void Hostile_files_are_rejected_as_unsafe(string fixture, string reason)
    {
        var bytes = (byte[])typeof(UnsafeFiles).GetMethod(fixture)!.Invoke(null, null)!;
        var r = DocxSafetyInspector.Inspect("resume.docx", bytes);
        r.Verdict.Should().Be(DocxVerdict.Unsafe);
        r.Reason.Should().Be(reason);
        r.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Write_protected_documents_count_as_password_protected() =>
        DocxSafetyInspector.Inspect("resume.docx", new DocxBuilder().Para("x").WriteProtected().Build())
            .Reason.Should().Be("password_protected");

    [Fact]
    public void Legacy_doc_and_corrupt_zip_are_unsupported()
    {
        DocxSafetyInspector.Inspect("resume.docx", UnsafeFiles.LegacyDoc()).Reason.Should().Be("legacy_doc");
        DocxSafetyInspector.Inspect("resume.docx", UnsafeFiles.CorruptZip()).Reason.Should().Be("corrupt");
    }

    [Fact]
    public void Files_over_5_MB_are_too_large()
    {
        var big = new byte[DocxLimits.MaxCompressedBytes + 1];
        "PK\u0003\u0004"u8.CopyTo(big);
        DocxSafetyInspector.Inspect("resume.docx", big).Verdict.Should().Be(DocxVerdict.TooLarge);
    }

    [Fact]
    public void A_zip_without_a_word_document_is_not_a_docx()
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(z.CreateEntry("hello.txt").Open()))
            w.Write("hi");
        DocxSafetyInspector.Inspect("resume.docx", ms.ToArray()).Reason.Should().Be("not_docx");
    }
}
