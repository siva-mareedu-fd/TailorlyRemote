using System.Collections.Concurrent;
using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Tailorly.Core.Ports;

namespace Tailorly.Tests.Infrastructure;

/// <summary>
/// Replaces Hangfire's client in tests: enqueued jobs are recorded and run on demand with <see cref="RunAllAsync"/>,
/// each in its own DI scope, exactly like the Hangfire server would (args carry ids only).
/// </summary>
public sealed class InlineJobClient(IServiceProvider services) : IBackgroundJobClient
{
    private readonly ConcurrentQueue<(string Id, Job Job)> _pending = new();
    private int _next;

    public ConcurrentQueue<Job> History { get; } = new();

    public string Create(Job job, IState state)
    {
        var id = Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _pending.Enqueue((id, job));
        History.Enqueue(job);
        return id;
    }

    public bool ChangeState(string jobId, IState state, string expectedState) => true;

    public int PendingCount => _pending.Count;

    /// <summary>Runs every pending job (and jobs they enqueue) until the queue is empty. Exceptions propagate.</summary>
    public async Task RunAllAsync(Func<Job, bool>? filter = null)
    {
        var deferred = new List<(string, Job)>();
        while (_pending.TryDequeue(out var item))
        {
            if (filter is not null && !filter(item.Job))
            {
                deferred.Add(item);
                continue;
            }
            await using var scope = services.CreateAsyncScope();
            var instance = scope.ServiceProvider.GetRequiredService(item.Job.Type);
            try
            {
                if (item.Job.Method.Invoke(instance, item.Job.Args.ToArray()) is Task t) await t;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }
        foreach (var d in deferred) _pending.Enqueue(d);
    }

    public void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
    }
}

/// <summary>Collects sent emails instead of talking to SMTP.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<EmailMessage> To(string address) =>
        Sent.Where(m => string.Equals(m.To, address, StringComparison.OrdinalIgnoreCase)).ToList();
}

/// <summary>Serilog sink that keeps every event fully rendered (message, properties, exception) for log-scan tests.</summary>
public sealed class CapturingLogSink : ILogEventSink
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Emit(LogEvent e)
    {
        var props = string.Join(" ", e.Properties.Select(p => $"{p.Key}={p.Value}"));
        Lines.Enqueue($"{e.Level} {e.MessageTemplate.Text} | {e.RenderMessage()} | {props} | {e.Exception}");
    }
}
