using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Tailorly.App.Infrastructure.Ai;
using Tailorly.App.Infrastructure.Fx;
using Tailorly.App.Infrastructure.Storage;
using Tailorly.Core.Ports;

namespace Tailorly.Tests.Unit;

public sealed class LocalFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tailorly-fs-" + Guid.NewGuid().ToString("N"));
    private readonly LocalFileStore _store;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LocalFileStoreTests() => _store = new LocalFileStore(Options.Create(new StorageOptions { RootPath = _root }));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("../escape.docx")]
    [InlineData("user/../../escape.docx")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("user\\..\\x")]
    [InlineData("")]
    public void Rejects_keys_that_could_escape_the_root(string key)
    {
        var act = () => _store.Resolve(key);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Saves_reads_and_deletes_by_key_and_prefix()
    {
        var userId = Guid.NewGuid();
        var key = $"{userId}/resumes/{Guid.NewGuid()}/original.docx";
        await _store.SaveAsync(key, new MemoryStream(Encoding.UTF8.GetBytes("hello")), Ct);

        await using (var s = await _store.OpenReadAsync(key, Ct))
            (await new StreamReader(s!).ReadToEndAsync(Ct)).Should().Be("hello");

        await _store.DeletePrefixAsync($"{userId}/", Ct);
        (await _store.OpenReadAsync(key, Ct)).Should().BeNull();
        await _store.DeleteAsync(key, Ct); // idempotent
    }
}

public sealed class FakeModelProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Returns_the_task_fixture_with_deterministic_usage()
    {
        var provider = new FakeModelProvider();
        var req = new ModelRequest("resume_parse", "claude-sonnet-5-5", "system", null,
            [new ModelMessage(ModelRole.User, "user text")], 4000, null, null);

        var a = await provider.SendAsync(req, Ct);
        var b = await provider.SendAsync(req, Ct);

        using var doc = JsonDocument.Parse(a.Text);
        doc.RootElement.GetProperty("totalYears").GetInt32().Should().Be(6);
        a.Usage.Should().Be(b.Usage);
        a.Usage.InputTokens.Should().BeGreaterThan(0);
        a.Model.Should().Be("claude-sonnet-5-5");
    }

    [Fact]
    public async Task Unknown_task_returns_empty_json_object()
    {
        var res = await new FakeModelProvider().SendAsync(
            new ModelRequest("nope", "m", "s", null, [new ModelMessage(ModelRole.User, "u")], 100, null, null), Ct);
        res.Text.Should().Be("{}");
    }

    [Fact]
    public async Task Scripted_replies_are_used_once_in_order_then_the_fixture()
    {
        var provider = new FakeModelProvider();
        provider.Enqueue("resume_parse", "first");
        provider.Enqueue("resume_parse", "second", stopReason: "max_tokens");
        var req = new ModelRequest("resume_parse", "m", "s", null, [new ModelMessage(ModelRole.User, "u")], 4000, null, null);

        (await provider.SendAsync(req, Ct)).Text.Should().Be("first");
        var second = await provider.SendAsync(req, Ct);
        second.Text.Should().Be("second");
        second.StopReason.Should().Be("max_tokens");
        (await provider.SendAsync(req, Ct)).Text.Should().Contain("totalYears");
        provider.Requests.Should().HaveCount(3);
    }

    [Fact]
    public void Counted_tokens_follow_the_budget_formula()
    {
        // input + output + cache_creation + 0.1 × cache_read (PRD §6.3)
        new ModelUsage(1000, 200, 300, 1000).CountedTokens.Should().Be(1600);
    }
}

public sealed class StaticFxRateProviderTests
{
    [Fact]
    public async Task Reads_configured_rates_and_returns_null_when_unknown()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fx:StaticRates:USD_INR"] = "84.5",
        }).Build();
        var fx = new StaticFxRateProvider(config);

        (await fx.GetRateAsync("usd", "inr", TestContext.Current.CancellationToken)).Should().Be(84.5m);
        (await fx.GetRateAsync("INR", "INR", TestContext.Current.CancellationToken)).Should().Be(1m);
        (await fx.GetRateAsync("EUR", "INR", TestContext.Current.CancellationToken)).Should().BeNull();
    }
}
