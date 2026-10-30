using Tailorly.App.Features.Jobs;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.App.Features.Digests;

// HTTP contract of ApiDesign §4.6 (digests and runs) and §4.10 (email links).

/// <param name="Kind">scheduled | manual.</param>
/// <param name="Status">queued | running | ready | failed (poll <c>GET /digests/{id}</c> for manual runs).</param>
/// <param name="Degraded">The rule-based fallback ranked some or all jobs (no AI scores for those).</param>
/// <param name="ItemCount">Jobs in the digest.</param>
public record DigestSummaryDto(
    Guid Id,
    string Kind,
    DateOnly LocalDate,
    string Status,
    string? ErrorCode,
    bool Degraded,
    int? JobsConsidered,
    int? JobsScored,
    int ItemCount,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? EmailedAt,
    DateTimeOffset CreatedAt)
{
    public static DigestSummaryDto From(Digest d, int itemCount) => new(d.Id, d.Kind, d.LocalDate, d.Status,
        d.ErrorCode, d.Degraded, d.JobsConsidered, d.JobsScored, itemCount, d.StartedAt, d.FinishedAt, d.EmailedAt, d.CreatedAt);
}

public sealed record DigestItemDto(int Rank, JobSummaryDto Job);

public sealed record DigestDetailDto(
    Guid Id,
    string Kind,
    DateOnly LocalDate,
    string Status,
    string? ErrorCode,
    bool Degraded,
    int? JobsConsidered,
    int? JobsScored,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? EmailedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DigestItemDto> Items)
{
    public static DigestDetailDto From(Digest d, IReadOnlyList<DigestItemDto> items) => new(d.Id, d.Kind,
        d.LocalDate, d.Status, d.ErrorCode, d.Degraded, d.JobsConsidered, d.JobsScored, d.StartedAt, d.FinishedAt, d.EmailedAt,
        d.CreatedAt, items);
}

public sealed record DigestPage(IReadOnlyList<DigestSummaryDto> Items, int Page, int PageSize, int Total);

public sealed record UnsubscribeResult(bool Done);

public sealed record VerifyNotificationEmailRequest(string? Token);
