using System.Text.Json.Serialization;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Features.Tailoring;
using Tailorly.Docx;
using Tailorly.Docx.Tailoring;

namespace Tailorly.Eval;

// ---------- dataset (datasets/tailoring.json; schema next to it) ----------

public sealed record TailoringParagraph(string Id, string Section, string Text, bool Editable, bool? Bullet, bool? Heading, string? Prefix);

public sealed record TailoringJobCase(string Title, string Company, string Description, IReadOnlyList<string> SkillsRequired,
    IReadOnlyList<string> SkillsPreferred, string? Seniority, int? YearsMin);

/// <param name="Forbidden">What this candidate does not have (skills, employers, credentials, metrics): in an unflagged edit,
/// any of them is a hallucination.</param>
public sealed record TailoringItem(string Id, string LabelSource, string? Notes, IReadOnlyList<TailoringParagraph> Paragraphs,
    IReadOnlyList<string> ConfirmedSkills, TailoringJobCase Job, IReadOnlyList<string> Gaps, IReadOnlyList<string> Forbidden);

public sealed record AdversarialEdit(string Item, string Paragraph, string NewText, string Kind);

public sealed record BenignEdit(string Item, string Paragraph, string NewText);

public sealed record TailoringDataset(int SchemaVersion, IReadOnlyList<TailoringItem> Items, IReadOnlyList<AdversarialEdit> Adversarial,
    IReadOnlyList<BenignEdit> Benign);

// ---------- metrics ----------

/// <summary>The deterministic guard on hand-written edits (no model): every adversarial edit refused or flagged, no benign one.</summary>
/// <param name="Slipped">Adversarial edits that passed as safe (target 0).</param>
/// <param name="FalsePositives">Benign rewordings that were refused or flagged.</param>
public sealed record GuardMetrics(int Adversarial, int Refused, int Flagged, IReadOnlyList<string> Slipped, int Benign,
    IReadOnlyList<string> FalsePositives);

/// <summary>A model's tailoring on the dataset (PRD §14.3 "tailoring hallucination rate, target 0 new facts").</summary>
/// <param name="Failed">Items whose tailoring failed after the repair turn (the user would see an error, not a fabrication).</param>
/// <param name="FlaggedNewFacts">Edits flagged <c>new_fact</c>: rejected by default, acceptable only once the user confirms the term.</param>
/// <param name="UnflaggedNewFacts">Edits that would pass as safe while adding a fact (target 0).</param>
/// <param name="Slips">Which edits and which facts (item, paragraph, the fact) for review.</param>
public sealed record TailorMetrics(int Items, int Completed, int Failed, int Edits, int FlaggedNewFacts, int UnflaggedNewFacts,
    IReadOnlyList<string> Slips, double? NewFactRate);

/// <summary>
/// The tailoring hallucination check (PRD §14.3). The model sees exactly what production sends (<see cref="ResumeTailorTask"/>
/// input builder, prompt, schema, validators with one repair turn), then an independent checker looks at every edit the
/// guard did not flag: a dataset term the candidate does not have, a job skill the résumé does not show, or a number the
/// résumé does not state is a hallucination that got through.
/// </summary>
public static class TailoringEval
{
    public static DocModel Model(TailoringItem item)
    {
        var paragraphs = item.Paragraphs.Select(p =>
        {
            var prefix = p.Prefix is { Length: > 0 } pre && p.Text.StartsWith(pre, StringComparison.Ordinal) ? pre.Length : 0;
            IReadOnlyList<DocRun> runs = prefix > 0
                ? [new DocRun(0, p.Text[..prefix], true, false, null, null) { FormatKey = "<w:b />" }, new DocRun(1, p.Text[prefix..], false, false, null, null)]
                : [new DocRun(0, p.Text, false, false, null, null)];
            var flags = new ParagraphFlags(p.Editable, p.Editable ? prefix : 0, false, false, false, p.Heading == true, p.Bullet == true,
                p.Editable ? null : p.Heading == true ? "heading" : "locked");
            return new DocParagraph(p.Id, ResumeExtractor.Fingerprint(ResumeExtractor.Normalize(p.Text)), p.Section, p.Text, runs, flags, null, null);
        }).ToList();
        var words = paragraphs.Sum(p => p.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        return new DocModel(paragraphs, [], new FormatFindings(false, false, false, false, false, false, ["Calibri"], words, 1, false,
            paragraphs.Count(p => p.Flags.Locked), paragraphs.Count(p => p.Flags.Editable), 0));
    }

    public static FactSource Facts(TailoringItem item) =>
        FactSource.Build(item.Paragraphs.Select(p => p.Text).Concat(item.ConfirmedSkills));

    public static EnrichmentOutput Enrichment(TailoringJobCase job) => new(job.SkillsRequired, job.SkillsPreferred, [], job.Seniority,
        job.YearsMin, null, "full_time", "remote", "worldwide", null, [], null, "employee", null,
        job.Description.Length > 300 ? job.Description[..300] : job.Description);

    public static TailorContext Context(TailoringItem item) =>
        new(Model(item), Facts(item), TailoringData.SkillGroups(Enrichment(item.Job), item.Gaps), new TailorRules());

    public static (TailoringJob Job, TailorTargets Targets) Inputs(TailoringItem item, KeywordsMissing missing) =>
        (new TailoringJob(item.Job.Title, item.Job.Company, item.Job.Description, Enrichment(item.Job)),
         new TailorTargets(item.Gaps.Select(g => $"Experience with {g}").ToList(), missing.Skills, item.Gaps.Select(g => (g, false)).ToList()));

    /// <summary>Job skills the résumé text does not show (the keyword part of the analysis).</summary>
    public sealed record KeywordsMissing(IReadOnlyList<string> Skills);

    public static KeywordsMissing Missing(TailoringItem item)
    {
        var lines = item.Paragraphs.Select(p => p.Text).ToList();
        var r = Tailorly.Core.Ats.KeywordCoverage.Compute(AtsScoring.Skills(Enrichment(item.Job)), lines);
        return new KeywordsMissing(r.Missing);
    }

    // ---------- the guard on hand-written edits (no model) ----------

    public static GuardMetrics Guard(TailoringDataset data)
    {
        var items = data.Items.ToDictionary(i => i.Id);
        int refused = 0, flagged = 0;
        var slipped = new List<string>();
        foreach (var a in data.Adversarial)
        {
            var (violations, checkedEdits) = Check(items[a.Item], a.Paragraph, a.NewText);
            if (violations.Count > 0) refused++;
            else if (checkedEdits.Count == 1 && checkedEdits[0].Flags.Contains(EditFlags.NewFact)) flagged++;
            else slipped.Add($"{a.Item}/{a.Paragraph} ({a.Kind})");
        }
        var falsePositives = new List<string>();
        foreach (var b in data.Benign)
        {
            var (violations, checkedEdits) = Check(items[b.Item], b.Paragraph, b.NewText);
            if (violations.Count > 0 || checkedEdits.Any(c => c.Flags.Contains(EditFlags.NewFact)))
                falsePositives.Add($"{b.Item}/{b.Paragraph}: {(violations.Count > 0 ? violations[0] : "new_fact " + string.Join(", ", checkedEdits[0].NewTerms))}");
        }
        return new GuardMetrics(data.Adversarial.Count, refused, flagged, slipped, data.Benign.Count, falsePositives);
    }

    private static (IReadOnlyList<string> Violations, IReadOnlyList<CheckedEdit> Checked) Check(TailoringItem item, string paragraph, string text)
    {
        var ctx = Context(item);
        var edit = new[] { new ProposedEdit(paragraph, text, "eval", false) };
        var v = EditValidator.Violations(edit, ctx);
        return (v, v.Count == 0 ? EditValidator.Check(edit, ctx) : []);
    }

    // ---------- the independent checker on model output ----------

    /// <summary>
    /// Facts an edit adds that the guard did not flag: dataset-labelled terms the candidate lacks, job skills the résumé
    /// and confirmed skills do not show, and numbers the résumé does not state. Flagged edits are not counted (they are
    /// rejected unless the user confirms the term).
    /// </summary>
    public static IReadOnlyList<string> UnflaggedFacts(CheckedEdit e, TailoringItem item)
    {
        if (e.Flags.Contains(EditFlags.NewFact)) return [];
        var facts = Facts(item);
        var proposed = TechText.Padded(e.Proposed);
        var original = TechText.Padded(e.Original);
        var found = new List<string>();
        foreach (var t in item.Forbidden.Where(t => TechText.ContainsPhrase(proposed, t) && !TechText.ContainsPhrase(original, t)
                                                    && !facts.HasPhrase(t)))
            found.Add($"forbidden '{t}'");
        foreach (var s in item.Job.SkillsRequired.Concat(item.Job.SkillsPreferred)
                     .Where(s => TechText.ContainsPhrase(proposed, s) && !facts.HasSkill(s)))
            found.Add($"job skill '{s}'");
        foreach (var n in FactGuard.NewNumbers(e.Proposed, facts)) found.Add($"number '{n.Raw}'");
        return found.Distinct().ToList();
    }
}
