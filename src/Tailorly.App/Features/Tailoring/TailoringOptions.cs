using Tailorly.Docx.Tailoring;

namespace Tailorly.App.Features.Tailoring;

/// <summary><c>Tailor:*</c> (PRD Appendix A, SDD §10.3).</summary>
public sealed class TailorOptions
{
    /// <summary>A proposal longer than this × the original paragraph is flagged <c>length</c>.</summary>
    public double MaxLengthRatio { get; set; } = 1.15;

    /// <summary>Edits per tailoring, so the diff stays reviewable.</summary>
    public int MaxEdits { get; set; } = 25;

    /// <summary>Allow I/my/we in edits of paragraphs that have none ("résumé norms").</summary>
    public bool AllowFirstPerson { get; set; }

    public TailorRules Rules => new(MaxLengthRatio, MaxEdits, AllowFirstPerson);
}

/// <summary><c>CoverLetter:*</c> (PRD F9, §8.4, D8): tones with their word limits, and phrases letters must avoid.</summary>
public sealed class CoverLetterOptions
{
    public const string DefaultTone = "concise";

    /// <summary>Tone → word limit. The tone list is configuration (D8); the database allows concise and confident.</summary>
    public Dictionary<string, int> Tones { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The clichés list (PRD §8.4). Empty configuration falls back to <see cref="DefaultCliches"/>.</summary>
    public List<string> Cliches { get; set; } = [];

    public IReadOnlyDictionary<string, int> EffectiveTones => Tones.Count > 0 ? Tones : DefaultTones;
    public IReadOnlyList<string> EffectiveCliches => Cliches.Count > 0 ? Cliches : DefaultCliches;

    // Defaults live here, not in the property initialisers: the configuration binder appends to a list's initial items.
    public static readonly IReadOnlyDictionary<string, int> DefaultTones = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["concise"] = 180,
        ["confident"] = 250,
    };

    public static readonly IReadOnlyList<string> DefaultCliches =
    [
        "I am writing to express my interest", "I am writing to apply", "To whom it may concern", "Dear Sir or Madam",
        "Dear Sir/Madam", "team player", "hard-working", "hardworking", "go-getter", "self-starter", "think outside the box",
        "results-driven", "detail-oriented", "perfect fit", "dream job", "synergy", "fast-paced environment",
        "passionate about", "I believe I would be a great fit", "wear many hats",
    ];
}

/// <summary><c>Pdf:*</c>: the Gotenberg service of the Compose <c>pdf</c> profile (SDD §10.6). Used only when the
/// <c>pdf_export.enabled</c> flag is on.</summary>
public sealed class PdfOptions
{
    public string GotenbergUrl { get; set; } = "http://gotenberg:3000";
    public int TimeoutSeconds { get; set; } = 60;
}
