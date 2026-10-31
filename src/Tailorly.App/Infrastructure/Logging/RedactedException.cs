using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Npgsql;

namespace Tailorly.App.Infrastructure.Logging;

/// <summary>
/// Stands in for an exception on its way to a log line or to Hangfire's job state. Library messages can echo their
/// input (System.Xml quotes an entity name, PostgreSQL a bad JSON token, a provider SDK its error body), so every message
/// in the chain is replaced unless Tailorly code threw it (those messages carry ids and codes only), or it is a
/// network or database-connection failure. PostgreSQL errors keep their SQLSTATE, table and constraint. Exception types
/// and stack traces are kept, so a failure can still be located.
/// </summary>
public sealed class RedactedException : Exception
{
    public const string Placeholder = "[message redacted]";

    private static readonly HashSet<Assembly> Ours =
    [
        typeof(RedactedException).Assembly,
        typeof(Tailorly.Core.ErrorCodes).Assembly,
        typeof(Tailorly.Docx.ResumeExtractor).Assembly,
    ];

    private readonly string _details;
    private readonly string? _stackTrace;

    // The message is the whole chain on one line ("Type: message ---> Inner: message"): Hangfire stores type, message and
    // its own stack walk, not ToString(), so the inner types would otherwise be lost there.
    private RedactedException(Exception original) : base(Summary(original))
    {
        _stackTrace = original.StackTrace;
        _details = Render(original);
    }

    public override string? StackTrace => _stackTrace;

    public override string ToString() => _details;

    public static RedactedException Of(Exception ex) => ex as RedactedException ?? new RedactedException(ex);

    /// <summary>The part of <paramref name="ex"/>'s message that may be written anywhere.</summary>
    public static string SafeMessage(Exception ex) => ex switch
    {
        RedactedException => ex.Message,
        PostgresException pg => $"SQLSTATE {pg.SqlState}" + (pg.TableName is { } t ? $", table {t}" : "") +
                                (pg.ConstraintName is { } c ? $", constraint {c}" : ""),
        NpgsqlException or HttpRequestException or SocketException or TimeoutException or OperationCanceledException => ex.Message,
        _ when ThrownByTailorly(ex) => ex.Message,
        _ => Placeholder,
    };

    // Where it was thrown, not its type: our code also throws framework types (InvalidOperationException), and a library
    // can throw a type it shares with us. An exception never thrown has no frames; then its type decides.
    private static bool ThrownByTailorly(Exception ex)
    {
        var trace = new StackTrace(ex, fNeedFileInfo: false);
        var origin = trace.FrameCount > 0 ? trace.GetFrame(0)?.GetMethod()?.DeclaringType : ex.GetType();
        return origin is not null && Ours.Contains(origin.Assembly);
    }

    private static string TypeName(Exception ex) => ex.GetType().FullName ?? ex.GetType().Name;

    private static IReadOnlyList<Exception> Inners(Exception ex) =>
        ex is AggregateException agg ? agg.InnerExceptions : ex.InnerException is { } inner ? [inner] : [];

    private static string Summary(Exception ex) => ex is RedactedException
        ? ex.Message
        : string.Join(" ---> ", Inners(ex).Select(Summary).Prepend($"{TypeName(ex)}: {SafeMessage(ex)}"));

    // The layout of Exception.ToString(): "Type: message ---> inner …   --- End of inner exception stack trace ---  at …".
    private static string Render(Exception ex)
    {
        var sb = new StringBuilder();
        Append(sb, ex);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, Exception ex)
    {
        if (ex is RedactedException r)
        {
            sb.Append(r._details);
            return;
        }
        sb.Append(TypeName(ex)).Append(": ").Append(SafeMessage(ex));
        foreach (var i in Inners(ex))
        {
            sb.Append(" ---> ");
            Append(sb, i);
            sb.AppendLine().Append("   --- End of inner exception stack trace ---");
        }
        if (ex.StackTrace is { } st)
            sb.AppendLine().Append(st);
    }
}
