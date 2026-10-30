namespace Tailorly.App.Features.Digests;

/// <summary>Configuration section <c>Enrichment</c> (SDD §8.5).</summary>
public sealed class EnrichmentOptions
{
    /// <summary>At most this many jobs are enriched by one run (best raw relevance first).</summary>
    public int MaxPerRun { get; set; } = 60;

    /// <summary>Concurrent enrichment calls in one run (the AI runner's global semaphore still applies).</summary>
    public int Parallelism { get; set; } = 4;
}

/// <summary>Configuration section <c>Scoring</c> (PRD §7.5).</summary>
public sealed class ScoringOptions
{
    /// <summary>Shortlist cap per run: ≈ 1K tokens × 20 × 30 days ≈ the 600K Discovery budget.</summary>
    public int MaxPerRun { get; set; } = 20;

    /// <summary>Concurrent scoring calls in one run (SDD §7.3: up to 4 in parallel).</summary>
    public int Parallelism { get; set; } = 4;
}

/// <summary>Configuration section <c>Digest</c> (PRD F7, SDD §11.1).</summary>
public sealed class DigestOptions
{
    /// <summary>A scheduled digest is still sent up to this long after the user's schedule time (late catch-up cap).</summary>
    public int WindowHours { get; set; } = 4;

    /// <summary>
    /// Retry delays after a failed attempt (SDD §11.1: 10/30/60 minutes), only while inside the window. Null by default:
    /// the configuration binder appends to an array's initial items, so the default lives in <see cref="RetryDelays"/>.
    /// </summary>
    public int[]? RetryDelayMinutes { get; set; }

    public IReadOnlyList<int> RetryDelays => RetryDelayMinutes is { Length: > 0 } d ? d : [10, 30, 60];

    /// <summary>"Run now" limit per user per local day (PRD F7).</summary>
    public int MaxManualRunsPerDay { get; set; } = 3;

    /// <summary>New jobs are looked for since the previous scheduled digest, but never further back than this.</summary>
    public int LookbackHours { get; set; } = 72;

    /// <summary>Jobs listed in one email; the rest are in the app.</summary>
    public int EmailMaxItems { get; set; } = 20;
}
