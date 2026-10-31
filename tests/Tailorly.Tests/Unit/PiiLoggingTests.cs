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
    [InlineData("FileDownloadName", true)]
    [InlineData("OriginalFilename", true)]
    [InlineData("FileName", true)]
    [InlineData("FileCount", false)]
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

    private static Exception Thrown(Action a)
    {
        try { a(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("expected a throw");
    }

    private static Exception XmlEcho(string text) => Thrown(() => System.Xml.Linq.XDocument.Parse($"<t>&{text};</t>"));

    [Fact]
    public void The_production_pipeline_logs_exception_types_and_stacks_but_not_library_messages()
    {
        var json = new StringWriter();
        var sink = new Capture(json);
        using (var logger = TailorlyLogging.Configure(new LoggerConfiguration(), [sink]).CreateLogger())
            logger.Error(new InvalidDataException("near 'quartzsecret'", XmlEcho("quartzsecret")), "Extract failed for {Id}", 7);

        var text = json.ToString();
        text.Should().NotContain("quartzsecret")
            .And.Contain("System.IO.InvalidDataException: [message redacted]")
            .And.Contain("System.Xml.XmlException: [message redacted]")
            .And.Contain("System.Xml.Linq.XDocument.Parse", "the stack trace is kept");
        sink.Events.Single().Exception.Should().BeOfType<RedactedException>();
    }

    [Fact]
    public void Messages_thrown_by_Tailorly_code_are_kept()
    {
        var ours = Thrown(() => Tailorly.Core.Time.TimeZones.RequireIana("Not/AZone"));
        RedactedException.SafeMessage(ours).Should().Be(ours.Message).And.NotBe(RedactedException.Placeholder);

        var neverThrown = new Tailorly.Core.Ports.SourceException(Tailorly.Core.Ports.SourceFailure.BadPayload, "Lever response is not an array");
        RedactedException.SafeMessage(neverThrown).Should().Be("Lever response is not an array");

        // The same framework type thrown by a library is redacted: where it was thrown decides, not the type.
        var library = Thrown(() => _ = new List<int>().First());
        library.Should().BeOfType<InvalidOperationException>();
        RedactedException.SafeMessage(library).Should().Be(RedactedException.Placeholder);
        RedactedException.SafeMessage(new InvalidOperationException("never thrown")).Should().Be(RedactedException.Placeholder);
    }

    [Fact]
    public void Postgres_errors_keep_only_their_code_table_and_constraint()
    {
        var pg = new Npgsql.PostgresException("invalid input syntax for type json: Token \"quartzsecret\" is invalid.", "ERROR", "ERROR",
            "22P02", detail: "Token \"quartzsecret\" is invalid.", tableName: "audit_log", constraintName: "pk_audit_log");
        RedactedException.SafeMessage(pg).Should().Be("SQLSTATE 22P02, table audit_log, constraint pk_audit_log");
        RedactedException.Of(new Exception("wrapper", pg)).ToString().Should().NotContain("quartzsecret");
    }

    [Fact]
    public void Network_and_connection_failures_keep_their_messages()
    {
        RedactedException.SafeMessage(new HttpRequestException("Connection refused (api.anthropic.com:443)"))
            .Should().Be("Connection refused (api.anthropic.com:443)");
        RedactedException.SafeMessage(new TimeoutException("The operation has timed out.")).Should().Be("The operation has timed out.");
        RedactedException.SafeMessage(new Npgsql.NpgsqlException("Failed to connect to 127.0.0.1:5432"))
            .Should().Be("Failed to connect to 127.0.0.1:5432");
    }

    [Fact]
    public void Every_inner_exception_of_an_aggregate_is_redacted_and_listed()
    {
        var agg = new AggregateException(XmlEcho("quartzone"), XmlEcho("quartztwo"));
        var text = RedactedException.Of(agg).ToString();
        text.Should().NotContain("quartzone").And.NotContain("quartztwo");
        text.Split("System.Xml.XmlException: [message redacted]").Should().HaveCount(3, "both inner exceptions are listed");
        RedactedException.Of(RedactedException.Of(agg)).ToString().Should().Be(text, "redacting twice changes nothing");
    }
}
