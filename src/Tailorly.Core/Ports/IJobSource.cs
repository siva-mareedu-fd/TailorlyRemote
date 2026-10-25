namespace Tailorly.Core.Ports;

/// <summary>
/// A job source adapter (PRD §7.1). Adapters only fetch and map to <see cref="RawJob"/>;
/// normalisation, dedupe and persistence are shared code.
/// </summary>
public interface IJobSource
{
    string SourceKey { get; }

    Task<IReadOnlyList<RawJob>> FetchAsync(FetchContext ctx, CancellationToken ct);
}

/// <summary>Context for one fetch run of one source.</summary>
/// <param name="Since">Only jobs newer than this are wanted (incremental fetch).</param>
/// <param name="Cursor">Last saved pagination cursor, if the source uses one.</param>
/// <param name="QueryHints">Optional query hints (roles, scope) a source may use to narrow the request.</param>
public sealed record FetchContext(
    DateTimeOffset Since,
    string? Cursor,
    IReadOnlyList<string> QueryHints);

/// <summary>A job as returned by a source, before normalisation. Unknown fields are ignored by adapters.</summary>
public sealed record RawJob
{
    public required string SourceKey { get; init; }
    public required string ExternalId { get; init; }
    public required string Title { get; init; }
    public required string CompanyName { get; init; }
    public required string ApplyUrl { get; init; }
    public required string SourceUrl { get; init; }
    public string? CompanyWebsite { get; init; }
    public string? LocationText { get; init; }
    public string? DescriptionHtml { get; init; }
    public string? EmploymentType { get; init; }
    public decimal? SalaryMin { get; init; }
    public decimal? SalaryMax { get; init; }
    public string? SalaryCurrency { get; init; }
    public string? SalaryPeriod { get; init; }
    public bool SalaryIsEstimate { get; init; }
    public DateTimeOffset? PostedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Structured source fields (e.g. Himalayas country/time-zone restrictions) stored as <c>jobs.source_meta</c>.</summary>
    public IReadOnlyDictionary<string, object?> SourceMeta { get; init; } = new Dictionary<string, object?>();
}
