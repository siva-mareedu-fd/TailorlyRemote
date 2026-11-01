using System.Net;
using System.Text.Json;
using FluentAssertions;
using Tailorly.App.Ai;
using Tailorly.Tests.Infrastructure;
using static Tailorly.Tests.Infrastructure.TailoringTestData;

namespace Tailorly.Tests.Integration.Tracker;

/// <summary>
/// The application tracker (PRD F11, ApiDesign §4.7, SDD §12.3): free movement between statuses with history, notes and
/// the concurrency token, the automatic move to Tailored (only from Saved), the list's filters, the snapshot that
/// survives the listing, and deletion with everything made for the entry.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TrackerTests : IAsyncLifetime
{
    private readonly PostgresAppFixture _fx;
    private TailorlyAppFactory F => _fx.Factory;
    private string Cs => _fx.ConnectionString;

    public TrackerTests(PostgresAppFixture fx) => _fx = fx;

    public ValueTask InitializeAsync()
    {
        F.Ai.ClearScript();
        F.Jobs.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        F.Ai.ClearScript();
        return ValueTask.CompletedTask;
    }

    private static async Task<JsonElement> GetAsync(TestUser u, Guid uj) =>
        await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs/{uj}"));

    private static async Task<HttpResponseMessage> PatchAsync(TestUser u, Guid uj, object body) =>
        await u.Api.SendJsonAsync(HttpMethod.Patch, $"/api/v1/user-jobs/{uj}", body);

    private static string[] History(JsonElement entry) =>
        entry.GetProperty("statusHistory").EnumerateArray().Select(h => h.GetProperty("status").GetString()!).ToArray();

    [Fact]
    public async Task Any_status_moves_to_any_other_with_history_notes_and_the_concurrency_token()
    {
        var (u, _, uj) = await TrackedJobAsync(F, "track-moves");
        var entry = await GetAsync(u, uj);
        entry.GetProperty("status").GetString().Should().Be("saved");
        History(entry).Should().Equal("saved");

        // Saved → Interviewing (a jump), → Saved (back), → Offer: no state machine.
        foreach (var next in new[] { "interviewing", "saved", "offer" })
        {
            var res = await PatchAsync(u, uj, new { status = next, concurrencyToken = entry.GetProperty("concurrencyToken").GetString() });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await ApiClient.TextAsync(res));
            entry = await ApiClient.JsonAsync(res);
            entry.GetProperty("status").GetString().Should().Be(next);
        }
        History(entry).Should().Equal("saved", "interviewing", "saved", "offer");

        // Notes alone keep the history; the same status again adds nothing; "" clears the notes.
        var stale = entry.GetProperty("concurrencyToken").GetString();
        entry = await ApiClient.JsonAsync(await PatchAsync(u, uj, new { notes = "Referred by Priya", concurrencyToken = stale }));
        entry.GetProperty("notes").GetString().Should().Be("Referred by Priya");
        entry = await ApiClient.JsonAsync(await PatchAsync(u, uj, new { status = "offer", concurrencyToken = entry.GetProperty("concurrencyToken").GetString() }));
        History(entry).Should().HaveCount(4);

        var conflict = await PatchAsync(u, uj, new { status = "rejected", concurrencyToken = stale });
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiClient.JsonAsync(conflict)).GetProperty("code").GetString().Should().Be("CONCURRENT_UPDATE");

        var token = entry.GetProperty("concurrencyToken").GetString();
        foreach (var bad in new object[]
                 {
                     new { status = "archived", concurrencyToken = token }, new { status = "applied" }, new { concurrencyToken = token },
                     new { notes = new string('n', 2_001), concurrencyToken = token },
                 })
        {
            var res = await PatchAsync(u, uj, bad);
            res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, JsonSerializer.Serialize(bad));
        }

        entry = await ApiClient.JsonAsync(await PatchAsync(u, uj, new { notes = "", concurrencyToken = token }));
        entry.GetProperty("notes").ValueKind.Should().Be(JsonValueKind.Null);
        entry.GetProperty("status").GetString().Should().Be("offer");
    }

    [Fact]
    public async Task Documents_move_an_entry_to_Tailored_only_from_Saved()
    {
        var (u, jobId, uj) = await TrackedJobAsync(F, "track-auto");
        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter());
        (await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await F.Jobs.RunAllAsync();
        var entry = await GetAsync(u, uj);
        entry.GetProperty("status").GetString().Should().Be("tailored");
        History(entry).Should().Equal("saved", "tailored");
        entry.GetProperty("documents").GetProperty("coverLetterStatus").GetString().Should().Be("ready");

        // Applied stays Applied when another document is made.
        entry = await ApiClient.JsonAsync(await PatchAsync(u, uj, new { status = "applied", concurrencyToken = entry.GetProperty("concurrencyToken").GetString() }));
        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter("confident"));
        (await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { tone = "confident" })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await F.Jobs.RunAllAsync();
        entry = await GetAsync(u, uj);
        entry.GetProperty("status").GetString().Should().Be("applied");
        History(entry).Should().Equal("saved", "tailored", "applied");
        jobId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_list_filters_by_status_and_text_and_sorts_by_score()
    {
        var (u, _, first) = await TrackedJobAsync(F, "track-list");
        var role = "Listrole" + Guid.NewGuid().ToString("N")[..6];
        var second = await AddByoAsync(u, $"{role} Platform Engineer", "Zeta Payments");
        var entry = await GetAsync(u, first);
        await PatchAsync(u, first, new { status = "applied", concurrencyToken = entry.GetProperty("concurrencyToken").GetString() });

        var all = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/user-jobs"));
        all.GetProperty("total").GetInt32().Should().Be(2);
        all.GetProperty("items")[0].GetProperty("id").GetGuid().Should().Be(first, "most recently updated first");

        var applied = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/user-jobs?status=applied&status=offer"));
        applied.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(first);
        var byText = await ApiClient.JsonAsync(await u.Api.GetAsync($"/api/v1/user-jobs?q={role.ToLowerInvariant()}"));
        byText.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(second);
        var wildcard = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/user-jobs?q=%25"));
        wildcard.GetProperty("total").GetInt32().Should().Be(0, "% is matched literally");

        var byScore = await ApiClient.JsonAsync(await u.Api.GetAsync("/api/v1/user-jobs?sort=-score&pageSize=1"));
        byScore.GetProperty("pageSize").GetInt32().Should().Be(1);
        byScore.GetProperty("items")[0].GetProperty("fitScore").ValueKind.Should().Be(JsonValueKind.Number, "scored entries come first");

        (await u.Api.GetAsync("/api/v1/user-jobs?status=archived")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await u.Api.GetAsync("/api/v1/user-jobs?sort=title")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task The_card_keeps_its_snapshot_after_the_listing_is_purged()
    {
        var (u, jobId, uj) = await TrackedJobAsync(F, "track-purge");
        var before = await GetAsync(u, uj);

        await Db.ExecAsync(Cs, "DELETE FROM jobs WHERE id = @id", ("id", jobId));

        var after = await GetAsync(u, uj);
        after.GetProperty("jobId").ValueKind.Should().Be(JsonValueKind.Null);
        foreach (var field in new[] { "title", "companyName", "applyUrl", "status" })
            after.GetProperty(field).GetString().Should().Be(before.GetProperty(field).GetString(), field);
        after.GetProperty("source").GetProperty("key").GetString().Should().Be(before.GetProperty("source").GetProperty("key").GetString());
        after.GetProperty("fitScore").GetInt32().Should().Be(before.GetProperty("fitScore").GetInt32());
        after.GetProperty("descriptionAvailable").GetBoolean().Should().BeFalse();
        (await ApiClient.TextAsync(await u.Api.GetAsync("/api/v1/user-jobs"))).Should().Contain(uj.ToString());
        (await u.Api.GetAsync($"/api/v1/jobs/{jobId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The AI actions ask for the description; pasting it makes the entry usable again.
        var refused = await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/interview-prep", new { });
        (await ApiClient.JsonAsync(refused)).GetProperty("code").GetString().Should().Be("JOB_DESCRIPTION_REQUIRED");
        (await u.Api.PutAsync($"/api/v1/user-jobs/{uj}/description", new { description = MatchingTestData.Description }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(u, uj)).GetProperty("descriptionAvailable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_an_entry_removes_its_documents_files_and_prep_but_not_the_listing()
    {
        var (u, jobId, uj) = await TrackedJobAsync(F, "track-delete");
        MatchingTestData.Reply(F, AiTaskNames.CoverLetter, Letter());
        var letter = await ApiClient.JsonAsync(await u.Api.PostAsync($"/api/v1/user-jobs/{uj}/cover-letters", new { }));
        await F.Jobs.RunAllAsync();
        await PrepAsync(F, u, uj);
        var key = await Db.ScalarAsync<string>(Cs, "SELECT storage_key FROM tailored_documents WHERE id = @id", ("id", letter.GetProperty("id").GetGuid()));
        File.Exists(Path.Combine(F.StorageRoot, key!)).Should().BeTrue();

        (await u.Api.DeleteAsync($"/api/v1/user-jobs/{uj}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await u.Api.GetAsync($"/api/v1/user-jobs/{uj}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        File.Exists(Path.Combine(F.StorageRoot, key!)).Should().BeFalse();
        foreach (var table in new[] { "tailored_documents", "interview_preps" })
            (await Db.ScalarAsync<long>(Cs, $"SELECT count(*) FROM {table} WHERE user_job_id = @id", ("id", uj))).Should().Be(0, table);
        (await Db.ScalarAsync<long>(Cs, "SELECT count(*) FROM jobs WHERE id = @id", ("id", jobId))).Should().Be(1, "the listing is shared");

        // The job can be tracked again from the shortlist.
        (await u.Api.PostAsync("/api/v1/user-jobs", new { jobId })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task<Guid> AddByoAsync(TestUser u, string title, string company)
    {
        var res = await u.Api.PostAsync("/api/v1/jobs/byo", new { description = ByoDescription, title, companyName = company });
        res.StatusCode.Should().Be(HttpStatusCode.Accepted, await ApiClient.TextAsync(res));
        F.Jobs.Clear(); // the enrichment is not part of these tests
        return (await ApiClient.JsonAsync(res)).GetProperty("id").GetGuid();
    }
}
