namespace Tailorly.Core.Ports;

/// <summary>
/// A job source adapter (PRD §7.1, SDD §8.0). Adapters only fetch and map to <see cref="RawJob"/>; scheduling, stop rules,
/// normalisation, dedupe and persistence are shared code. The seam has two shapes, one per discovery path:
/// <see cref="IFeedSource"/> (Path A, newest jobs across companies) and <see cref="IBoardSource"/> (Path B, one company
/// board at a time). Failures are reported as <see cref="SourceException"/>.
/// </summary>
public interface IJobSource
{
    /// <summary>The <c>job_sources.key</c> this adapter serves (e.g. <c>remoteok</c>, <c>greenhouse</c>).</summary>
    string SourceKey { get; }
}

/// <summary>
/// Path A: a feed that pages newest-first. The shared <c>FeedRunner</c> walks each stream from the top and applies the stop
/// rules (older than <c>since</c>, an all-known page, the page cap).
/// </summary>
public interface IFeedSource : IJobSource
{
    /// <summary>Independent newest-first listings this source walks (e.g. two Himalayas searches). Most feeds have one.</summary>
    IReadOnlyList<string> Streams { get; }

    /// <summary>One page of <paramref name="stream"/>; <paramref name="cursor"/> is null for the newest page.</summary>
    Task<FeedPage> GetNewestPageAsync(string stream, string? cursor, CancellationToken ct);
}

/// <param name="Jobs">Mapped jobs in feed order.</param>
/// <param name="NextCursor">Cursor of the next (older) page, or null at the end of the stream.</param>
/// <param name="SkippedRows">Items that could not be mapped (missing id, title, company or URL); counted, never fatal.</param>
public sealed record FeedPage(IReadOnlyList<RawJob> Jobs, string? NextCursor, int SkippedRows = 0);

/// <summary>
/// Path B: an employer's ATS board (Greenhouse, Lever, Ashby). Board tokens are validated against
/// <c>^[a-z0-9_-]{2,60}$</c> and only ever placed into the adapter's fixed URL template on its allowlisted host.
/// </summary>
public interface IBoardSource : IJobSource
{
    /// <summary>
    /// Lists the board's open jobs. When <paramref name="withContent"/> is false the platform may omit descriptions
    /// (Greenhouse), and <see cref="BoardListing.HasDescriptions"/> says so. A missing board throws
    /// <see cref="SourceException"/> with <see cref="SourceFailure.NotFound"/>.
    /// </summary>
    Task<BoardListing> ListAsync(string boardToken, bool withContent, CancellationToken ct);

    /// <summary>One job with its description (only called when the listing has none). Null if it no longer exists.</summary>
    Task<RawJob?> GetJobAsync(string boardToken, string externalId, CancellationToken ct);
}

/// <param name="CompanyName">The board's company name when the platform supplies it.</param>
public sealed record BoardListing(string? CompanyName, IReadOnlyList<RawJob> Jobs, bool HasDescriptions, int SkippedRows = 0);

/// <summary>A job as returned by a source, before normalisation. Unknown fields are ignored by adapters.</summary>
public sealed record RawJob
{
    public required string SourceKey { get; init; }

    /// <summary>Unique within the source. Board adapters prefix the board token (<c>{token}:{id}</c>).</summary>
    public required string ExternalId { get; init; }

    public required string Title { get; init; }
    public required string CompanyName { get; init; }

    /// <summary>The apply link exactly as the source supplied it (kept for sources with <c>require_source_apply_url</c>).</summary>
    public required string ApplyUrl { get; init; }

    /// <summary>The listing page on the source (the attribution link).</summary>
    public required string SourceUrl { get; init; }

    public string? CompanyWebsite { get; init; }
    public string? LocationText { get; init; }

    /// <summary>Workplace type as published (<c>remote</c>, <c>Hybrid</c>, <c>OnSite</c>…), if the source has one.</summary>
    public string? WorkplaceType { get; init; }

    /// <summary>
    /// Structured country restrictions (Himalayas): null = the source has no structured field; empty = open worldwide;
    /// otherwise the countries a candidate may live in.
    /// </summary>
    public IReadOnlyList<string>? CountryRestrictions { get; init; }

    public string? DescriptionHtml { get; init; }

    /// <summary>The source cut the description short (e.g. Remote OK teasers ending in "..."), so it is not full text.</summary>
    public bool DescriptionIsPartial { get; init; }

    public string? EmploymentType { get; init; }
    public decimal? SalaryMin { get; init; }
    public decimal? SalaryMax { get; init; }
    public string? SalaryCurrency { get; init; }

    /// <summary>As published: hour, day, week, fortnight, month or year (adapters map the source's wording).</summary>
    public string? SalaryPeriod { get; init; }

    public bool SalaryIsEstimate { get; init; }
    public DateTimeOffset? PostedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Structured source fields (e.g. Himalayas time zones) stored as <c>jobs.source_meta</c>.</summary>
    public IReadOnlyDictionary<string, object?> SourceMeta { get; init; } = new Dictionary<string, object?>();
}

/// <summary>Why a source call failed. Drives the resilience rules of PRD §7.2.</summary>
public enum SourceFailure
{
    /// <summary>404/410: the board or job does not exist. Never fatal: mark the link <c>not_found</c> and move on.</summary>
    NotFound,

    /// <summary>429/403: back off (honour <c>Retry-After</c>) and open the source's circuit.</summary>
    Throttled,

    /// <summary>402, any other 4xx, 5xx after retries, timeouts, network errors: unavailable for this run.</summary>
    Unavailable,

    /// <summary>The response could not be parsed (bad JSON/XML or a changed schema).</summary>
    BadPayload,
}

/// <summary>A failed source call. Messages carry status codes and short reasons only, never payloads.</summary>
public sealed class SourceException(SourceFailure failure, string message, int? statusCode = null, TimeSpan? retryAfter = null,
    Exception? inner = null) : Exception(message, inner)
{
    public SourceFailure Failure { get; } = failure;
    public int? StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
