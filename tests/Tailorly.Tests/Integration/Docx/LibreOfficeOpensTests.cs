using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Tailorly.App.Features.Tailoring;
using Tailorly.Docx;
using Tailorly.Docx.Tailoring;
using Tailorly.Tests.Infrastructure;
using Tailorly.Tests.Unit.Docx;

namespace Tailorly.Tests.Integration.Docx;

/// <summary>Gotenberg (LibreOffice behind an HTTP API, the Compose <c>pdf</c> profile's container) for the whole class.</summary>
public sealed class GotenbergFixture : IAsyncLifetime
{
    public const string Image = "gotenberg/gotenberg:8";

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(3000, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3000).ForPath("/health")))
        .Build();

    public PdfExport Converter { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(3000)}/"),
            Timeout = TimeSpan.FromSeconds(120),
        };
        Converter = new PdfExport(http);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// LibreOffice opens every fixture before and after tailoring (PRD §14.1 "open result in LibreOffice headless to confirm it
/// opens"), and the cover letter. LibreOffice runs in Gotenberg, the same converter the PDF export uses, so this also
/// exercises <see cref="PdfExport"/> against the real service. A document LibreOffice cannot open fails the conversion.
/// </summary>
public sealed class LibreOfficeOpensTests(GotenbergFixture gotenberg) : IClassFixture<GotenbergFixture>
{
    public static TheoryData<string> Fixtures() => new(ResumeFixtures.All.Select(f => f.Name));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task LibreOffice_opens_the_fixture_before_and_after_every_editable_paragraph_is_rewritten(string name)
    {
        var original = ResumeFixtures.Get(name).Docx;
        var model = ResumeExtractor.Extract(new MemoryStream(original));
        var edited = EditApplier.Apply(original, model.Paragraphs.Where(p => p.Flags.Editable)
            .Select(p => new EditToApply(p.Id, p.Fingerprint, FormatPreservationTests.Rewrite(p))).ToList());

        foreach (var docx in new[] { original, edited })
        {
            var pdf = await gotenberg.Converter.ConvertAsync(docx, TestContext.Current.CancellationToken);
            pdf.Length.Should().BeGreaterThan(1000, $"{name} converts to a real PDF");
        }
    }

    /// <summary>Opt-in: the owner's own résumés (<c>TAILORLY_LOCAL_RESUMES</c>), never committed; counts only.</summary>
    [Fact]
    public async Task LibreOffice_opens_local_resumes_after_every_editable_paragraph_is_rewritten()
    {
        var dir = Environment.GetEnvironmentVariable("TAILORLY_LOCAL_RESUMES");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Assert.Skip("Set TAILORLY_LOCAL_RESUMES to run against local résumé files.");
            return;
        }
        var files = Directory.GetFiles(dir, "*.docx");
        for (var n = 0; n < files.Length; n++)
        {
            var original = await File.ReadAllBytesAsync(files[n], TestContext.Current.CancellationToken);
            var model = ResumeExtractor.Extract(new MemoryStream(original));
            var edited = EditApplier.Apply(original, model.Paragraphs.Where(p => p.Flags.Editable)
                .Select(p => new EditToApply(p.Id, p.Fingerprint, FormatPreservationTests.Rewrite(p))).ToList());
            var pdf = await gotenberg.Converter.ConvertAsync(edited, TestContext.Current.CancellationToken);
            pdf.Length.Should().BeGreaterThan(1000, $"local résumé #{n + 1}");
            TestContext.Current.SendDiagnosticMessage($"local résumé #{n + 1}: LibreOffice opened the tailored copy ({pdf.Length} PDF bytes)");
        }
    }

    [Fact]
    public async Task LibreOffice_opens_the_cover_letter()
    {
        var docx = CoverLetterWriter.Write(["Hello Acme Cloud team,", "I build .NET & PostgreSQL services <for payments>.", "Best regards,", "Asha Verma"]);
        (await gotenberg.Converter.ConvertAsync(docx, TestContext.Current.CancellationToken)).Length.Should().BeGreaterThan(1000);
    }
}
