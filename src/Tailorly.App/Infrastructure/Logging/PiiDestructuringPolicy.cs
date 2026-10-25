using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace Tailorly.App.Infrastructure.Logging;

/// <summary>
/// Destructures objects logged with <c>{@Obj}</c> while dropping every property whose name is blocked by
/// <see cref="PiiFields"/>. Scalars, strings and collections fall through to Serilog's defaults.
/// </summary>
public sealed class PiiDestructuringPolicy : IDestructuringPolicy
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Cache = new();

    public bool TryDestructure(object value, ILogEventPropertyValueFactory factory, out LogEventPropertyValue result)
    {
        var type = value.GetType();
        if (IsSimple(type) || value is IEnumerable)
        {
            result = null!;
            return false;
        }

        var props = Cache.GetOrAdd(type, t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !PiiFields.IsBlocked(p.Name))
            .ToArray());

        var list = new List<LogEventProperty>(props.Length);
        foreach (var p in props)
        {
            object? v;
            try { v = p.GetValue(value); }
            catch (TargetInvocationException) { continue; }
            list.Add(new LogEventProperty(p.Name, factory.CreatePropertyValue(v, destructureObjects: true)));
        }

        result = new StructureValue(list, type.Name);
        return true;
    }

    private static bool IsSimple(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid) ||
        t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) ||
        t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(Uri) ||
        Nullable.GetUnderlyingType(t) is not null;
}
