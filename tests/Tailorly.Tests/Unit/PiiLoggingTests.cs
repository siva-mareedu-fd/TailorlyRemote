using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Tailorly.App.Infrastructure.Logging;

namespace Tailorly.Tests.Unit;

public sealed class PiiLoggingTests
{
    /// <summary>Keeps events and also writes them exactly as production does (compact JSON).</summary>
    private sealed class Capture(TextWriter json) : ILogEventSink
    {
        private readonly CompactJsonFormatter _formatter = new();
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
            _formatter.Format(logEvent, json);
        }
    }

    private static (Logger Logger, Capture Sink, StringWriter Json) Create()
    {
        var json = new StringWriter();
        var sink = new Capture(json);
        var logger = new LoggerConfiguration()
            .Destructure.With<PiiDestructuringPolicy>()
            .Enrich.With<PiiScrubbingEnricher>()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink, json);
    }

    [Theory]
    [InlineData("ResumeText", true)]
    [InlineData("resumeId", true)]
    [InlineData("DescriptionText", true)]
    [InlineData("ContentLength", true)]
    [InlineData("Email", true)]
    [InlineData("prompt", true)]
    [InlineData("Response", true)]
    [InlineData("UserId", false)]
    [InlineData("SizeBytes", false)]
    [InlineData("NotificationEmail", true)]
    [InlineData("EmailConfirmed", false)]
    [InlineData("QueryString", true)]
    [InlineData("UnsubscribeToken", true)]
    [InlineData("CountedTokens", false)]
    public void Blocklist_matches_the_documented_names(string name, bool blocked) =>
        PiiFields.IsBlocked(name).Should().Be(blocked);

    [Fact]
    public void Template_properties_with_blocked_names_are_dropped()
    {
        var (logger, sink, json) = Create();
        var userId = Guid.NewGuid();

        logger.Information("Signed up {Email} as {UserId}", "ravi@example.test", userId);
        logger.Dispose();

        var e = sink.Events.Single();
        e.Properties.Should().ContainKey("UserId").And.NotContainKey("Email");
        json.ToString().Should().NotContain("ravi@example.test").And.Contain(userId.ToString());
    }

    [Fact]
    public void Destructured_objects_lose_blocked_fields_at_every_level()
    {
        var (logger, sink, json) = Create();

        logger.Information("Payload {@Payload}", new
        {
            UserId = 42,
            ResumeText = "SECRET-RESUME",
            Prompt = "SECRET-PROMPT",
            Nested = new { Description = "SECRET-JOB", SizeBytes = 1234 },
        });
        logger.Dispose();

        var payload = (StructureValue)sink.Events.Single().Properties["Payload"];
        payload.Properties.Select(p => p.Name).Should().BeEquivalentTo("UserId", "Nested");
        var nested = (StructureValue)payload.Properties.Single(p => p.Name == "Nested").Value;
        nested.Properties.Select(p => p.Name).Should().BeEquivalentTo("SizeBytes");
        json.ToString().Should().NotContain("SECRET");
    }

    [Fact]
    public void Dictionaries_lose_blocked_keys()
    {
        var (logger, _, json) = Create();
        logger.Information("Data {@Data}", new Dictionary<string, object> { ["email"] = "x@example.test", ["count"] = 3 });
        logger.Dispose();
        json.ToString().Should().NotContain("x@example.test").And.Contain("count");
    }
}
