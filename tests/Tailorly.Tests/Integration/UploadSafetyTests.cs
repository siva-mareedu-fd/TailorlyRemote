using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Integration;

/// <summary>POST /resumes refuses hostile and wrong files before anything is stored (SDD §10.5, ApiDesign §12).</summary>
[Collection(PostgresCollection.Name)]
public sealed class UploadSafetyTests(PostgresAppFixture fx)
{
    private TailorlyAppFactory F => fx.Factory;

    private async Task AssertNothingStored(TestUser u)
    {
        (await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/resumes"))).GetArrayLength().Should().Be(0);
        Directory.Exists(Path.Combine(F.StorageRoot, u.Id.ToString("D"))).Should().BeFalse();
    }

    [Theory]
    [InlineData("resume.docx")]
    [InlineData("resume.pdf")]
    public async Task Pdf_is_unsupported_with_the_friendly_message(string fileName)
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "pdf");
        var res = await u.Api.UploadAsync("/api/v1/resumes", fileName, UnsafeFiles.Pdf(), "application/pdf");
        res.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        var body = await ApiClient.JsonAsync(res);
        body.GetProperty("code").GetString().Should().Be("UNSUPPORTED_FILE_TYPE");
        body.GetProperty("detail").GetString().Should().Be("Please upload the Word (.docx) version of your résumé.");
        await AssertNothingStored(u);
    }

    public static TheoryData<string, string> Unsafe => new()
    {
        { nameof(UnsafeFiles.WithMacroPart), "macros" },
        { nameof(UnsafeFiles.WithOleObject), "embedded_object" },
        { nameof(UnsafeFiles.WithExternalTemplate), "external_relationship" },
        { nameof(UnsafeFiles.ZipBomb), "zip_bomb" },
        { nameof(UnsafeFiles.TooManyEntries), "too_many_entries" },
        { nameof(UnsafeFiles.WithNestedArchive), "nested_archive" },
        { nameof(UnsafeFiles.PasswordProtected), "password_protected" },
        { nameof(UnsafeFiles.WithDoctype), "bad_xml" },
    };

    [Theory]
    [MemberData(nameof(Unsafe))]
    public async Task Hostile_docx_files_get_FILE_UNSAFE(string fixture, string reason)
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "unsafe");
        var bytes = (byte[])typeof(UnsafeFiles).GetMethod(fixture)!.Invoke(null, null)!;
        var res = await u.Api.UploadAsync("/api/v1/resumes", "resume.docx", bytes);
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await ApiClient.JsonAsync(res);
        body.GetProperty("code").GetString().Should().Be("FILE_UNSAFE");
        body.GetProperty("extra").GetProperty("reason").GetString().Should().Be(reason);
        await AssertNothingStored(u);
    }

    [Fact]
    public async Task Docm_is_refused_as_unsafe()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "docm");
        var res = await u.Api.UploadAsync("/api/v1/resumes", "resume.docm", DocxBuilder.SampleResume());
        (await ApiClient.CodeAsync(res)).Should().Be("FILE_UNSAFE");
    }

    [Fact]
    public async Task Files_over_5_MB_get_413()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "big");
        var big = new byte[5 * 1024 * 1024 + 10];
        "PK\u0003\u0004"u8.CopyTo(big);
        var res = await u.Api.UploadAsync("/api/v1/resumes", "resume.docx", big);
        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await ApiClient.CodeAsync(res)).Should().Be("FILE_TOO_LARGE");
        await AssertNothingStored(u);
    }

    [Fact]
    public async Task Missing_file_and_wrong_content_type_are_rejected()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "nofile");
        var empty = await u.Api.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/resumes")
            { Content = new MultipartFormDataContent { { new StringContent("x"), "other" } } });
        empty.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var json = await u.Api.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/resumes") { Content = JsonContent.Create(new { file = "x" }) });
        json.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Upload_without_the_csrf_header_is_refused()
    {
        var u = await TestUsers.CreateVerifiedAsync(F, "csrf");
        var form = new MultipartFormDataContent { { new ByteArrayContent(DocxBuilder.SampleResume()), "file", "r.docx" } };
        var res = await u.Api.Http.PostAsync("/api/v1/resumes", form, TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApiClient.CodeAsync(res)).Should().Be("CSRF_INVALID");
    }
}
