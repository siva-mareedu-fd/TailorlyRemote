using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Tailorly.App.Infrastructure.Logging;

/// <summary>
/// The one Serilog pipeline of the process (SDD §14.3, §16): PII-named properties dropped, exception messages redacted
/// (<see cref="RedactedException"/>), compact JSON to stdout, plus any extra sinks (the log-scan tests register a
/// capturing sink in DI). Every sink sits behind the redaction, so no sink sees a raw exception.
/// </summary>
public static class TailorlyLogging
{
    public static LoggerConfiguration Configure(LoggerConfiguration lc, IEnumerable<ILogEventSink> extraSinks) => lc
        .Enrich.FromLogContext()
        .Destructure.With<PiiDestructuringPolicy>()
        .Enrich.With<PiiScrubbingEnricher>()
        .WriteTo.Sink(LoggerSinkConfiguration.Wrap(sink => new ExceptionRedactingSink(sink), w =>
        {
            w.Console(new CompactJsonFormatter());
            foreach (var sink in extraSinks)
                w.Sink(sink);
        }));
}

/// <summary>Hands the wrapped sinks a copy of each event whose exception is a <see cref="RedactedException"/>.</summary>
public sealed class ExceptionRedactingSink(ILogEventSink inner) : ILogEventSink, IDisposable
{
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Exception is null or RedactedException)
        {
            inner.Emit(logEvent);
            return;
        }
        inner.Emit(new LogEvent(logEvent.Timestamp, logEvent.Level, RedactedException.Of(logEvent.Exception),
            logEvent.MessageTemplate, logEvent.Properties.Select(p => new LogEventProperty(p.Key, p.Value)),
            logEvent.TraceId ?? default, logEvent.SpanId ?? default));
    }

    public void Dispose() => (inner as IDisposable)?.Dispose();
}
