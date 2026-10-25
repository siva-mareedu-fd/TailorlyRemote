using Serilog.Core;
using Serilog.Events;

namespace Tailorly.App.Infrastructure.Logging;

/// <summary>
/// Removes blocked properties from every log event, including template properties such as
/// <c>{Email}</c> and nested structures from any source (e.g. framework code). Runs last in the pipeline.
/// The JSON output writes the message template, not the rendered message, so removed values never appear.
/// </summary>
public sealed class PiiScrubbingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        List<string>? remove = null;
        List<LogEventProperty>? replace = null;

        foreach (var (name, value) in logEvent.Properties)
        {
            if (PiiFields.IsBlocked(name))
            {
                (remove ??= []).Add(name);
                continue;
            }
            var scrubbed = Scrub(value);
            if (!ReferenceEquals(scrubbed, value))
                (replace ??= []).Add(new LogEventProperty(name, scrubbed));
        }

        if (remove is not null)
            foreach (var n in remove) logEvent.RemovePropertyIfPresent(n);
        if (replace is not null)
            foreach (var p in replace) logEvent.AddOrUpdateProperty(p);
    }

    private static LogEventPropertyValue Scrub(LogEventPropertyValue value) => value switch
    {
        StructureValue s when s.Properties.Any(p => PiiFields.IsBlocked(p.Name) || NeedsScrub(p.Value)) =>
            new StructureValue(s.Properties
                .Where(p => !PiiFields.IsBlocked(p.Name))
                .Select(p => new LogEventProperty(p.Name, Scrub(p.Value))), s.TypeTag),
        DictionaryValue d when d.Elements.Any(e => IsBlockedKey(e.Key) || NeedsScrub(e.Value)) =>
            new DictionaryValue(d.Elements
                .Where(e => !IsBlockedKey(e.Key))
                .Select(e => new KeyValuePair<ScalarValue, LogEventPropertyValue>(e.Key, Scrub(e.Value)))),
        SequenceValue q when q.Elements.Any(NeedsScrub) =>
            new SequenceValue(q.Elements.Select(Scrub)),
        _ => value,
    };

    private static bool NeedsScrub(LogEventPropertyValue v) => !ReferenceEquals(Scrub(v), v);

    private static bool IsBlockedKey(ScalarValue key) => key.Value is string s && PiiFields.IsBlocked(s);
}
