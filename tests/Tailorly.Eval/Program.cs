// Tailorly.Eval: prompt/model evaluation harness (PRD §14.3, SDD §9.4).
//
//   dotnet run --project tests/Tailorly.Eval                                   validate datasets, rules baseline only
//   dotnet run --project tests/Tailorly.Eval -- --models <id>[,<id>...]         also run each model (needs Anthropic__ApiKey)
//
// Options: --tasks eligibility,fit,tailor (default all) · --limit N (first N items per dataset) · --effort low|medium|high
//          --datasets <dir> · --out <dir> (default tests/Tailorly.Eval/reports) · --no-write (print only)
// Model ids are passed on the command line, never hard-coded: production routing lives only in ai_task_config.
// The tailoring guard check (hand-written adversarial and benign edits) always runs; the exit code is 1 if an
// adversarial edit slips through, so CI fails on a guard regression.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Anthropic;
using Tailorly.App.Ai;
using Tailorly.App.Ai.Tasks;
using Tailorly.App.Infrastructure.Ai;
using Tailorly.Core.Jobs;
using Tailorly.Core.Matching;
using Tailorly.Eval;

var options = EvalOptions.Parse(args);
if (options.Error is { } error)
{
    Console.Error.WriteLine(error);
    return 2;
}

var (eligibility, eligibilityErrors) = await Datasets.LoadAsync<EligibilityDataset>(options.DatasetsDir, "eligibility");
var (fit, fitErrors) = await Datasets.LoadAsync<FitDataset>(options.DatasetsDir, "fit_scores");
var (tailoring, tailoringErrors) = await Datasets.LoadAsync<TailoringDataset>(options.DatasetsDir, "tailoring");
var datasetErrors = eligibilityErrors.Concat(fitErrors).Concat(tailoringErrors).ToList();
if (fit is not null)
    datasetErrors.AddRange(fit.Items.Where(i => !fit.Candidates.ContainsKey(i.CandidateId))
        .Select(i => $"fit_scores: item {i.Id} names unknown candidate {i.CandidateId}"));
if (tailoring is not null)
{
    var editable = tailoring.Items.SelectMany(i => i.Paragraphs.Where(p => p.Editable).Select(p => (i.Id, p.Id))).ToHashSet();
    datasetErrors.AddRange(tailoring.Adversarial.Select(a => (a.Item, a.Paragraph)).Concat(tailoring.Benign.Select(b => (b.Item, b.Paragraph)))
        .Where(r => !editable.Contains(r)).Select(r => $"tailoring: {r.Item}/{r.Paragraph} is not an editable paragraph"));
}
if (datasetErrors.Count > 0)
{
    foreach (var e in datasetErrors) Console.Error.WriteLine(e);
    return 1;
}

var eligibilityItems = eligibility!.Items.Take(options.Limit).ToList();
var fitItems = fit!.Items.Take(options.Limit).ToList();
var tailoringItems = tailoring!.Items.Take(options.Limit).ToList();
Console.WriteLine($"Datasets OK: {eligibility.Items.Count} eligibility items, {fit.Items.Count} fit pairs, " +
                  $"{tailoring.Items.Count} tailoring cases with {tailoring.Adversarial.Count} adversarial and {tailoring.Benign.Count} benign edits " +
                  $"({eligibility.Items.Count(i => i.LabelSource.Contains("unreviewed", StringComparison.OrdinalIgnoreCase)) + fit.Items.Count(i => i.LabelSource.Contains("unreviewed", StringComparison.OrdinalIgnoreCase)) + tailoring.Items.Count(i => i.LabelSource.Contains("unreviewed", StringComparison.OrdinalIgnoreCase))} labels unreviewed).");

var prompts = new PromptRegistry();
var report = new EvalReport
{
    RunAt = DateTimeOffset.UtcNow,
    PromptVersions = new Dictionary<string, string>
    {
        [AiTaskNames.JobEnrichment] = prompts.Get(AiTaskNames.JobEnrichment).Version,
        [AiTaskNames.JobFitScore] = prompts.Get(AiTaskNames.JobFitScore).Version,
        [AiTaskNames.ResumeTailor] = prompts.Get(AiTaskNames.ResumeTailor).Version,
    },
    EligibilityItems = eligibilityItems.Count,
    FitItems = fitItems.Count,
    TailoringItems = tailoringItems.Count,
    // The deterministic guard on hand-written edits: no model, so it runs everywhere (CI included).
    Guard = TailoringEval.Guard(tailoring),
};

// Deterministic baseline: the pre-rules alone (what the app falls back to without enrichment).
report.Runs.Add(new EvalRun
{
    Model = "rules (no model)",
    Eligibility = Metrics.ForEligibility(eligibilityItems
        .Select(i => (i, (string?)EligibilityRules.Classify(i.Countries, i.LocationText, i.Title, i.Description).Eligibility)).ToList()),
});

var key = Environment.GetEnvironmentVariable("Anthropic__ApiKey") ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (options.Models.Count == 0 || string.IsNullOrWhiteSpace(key))
{
    Console.WriteLine(options.Models.Count == 0
        ? "No --models given: rules baseline only."
        : "No API key (Anthropic__ApiKey): rules baseline only.");
}
else
{
    var provider = new AnthropicModelProvider(new AnthropicClient { ApiKey = key, MaxRetries = 3, Timeout = TimeSpan.FromSeconds(120) });
    var runner = new ModelRunner(provider, prompts, options.Effort);
    foreach (var model in options.Models)
    {
        Console.WriteLine($"Running {model}…");
        var run = new EvalRun { Model = model };
        if (options.Tasks.Contains("eligibility")) await RunEligibilityAsync(runner, model, eligibilityItems, run);
        if (options.Tasks.Contains("fit")) await RunFitAsync(runner, model, fit, fitItems, run);
        if (options.Tasks.Contains("tailor")) await RunTailorAsync(runner, model, tailoringItems, run);
        report.Runs.Add(run);
    }
}

var markdown = report.ToMarkdown();
Console.WriteLine();
Console.WriteLine(markdown);
if (options.Write)
{
    Directory.CreateDirectory(options.OutDir);
    var stamp = report.RunAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    await File.WriteAllTextAsync(Path.Combine(options.OutDir, $"eval-{stamp}.json"), JsonSerializer.Serialize(report, Datasets.Json));
    await File.WriteAllTextAsync(Path.Combine(options.OutDir, $"eval-{stamp}.md"), markdown);
    Console.WriteLine($"Report written to {options.OutDir}");
}
if (report.Guard.Slipped.Count > 0)
{
    Console.Error.WriteLine($"Tailoring guard: {report.Guard.Slipped.Count} adversarial edit(s) passed as safe: {string.Join(", ", report.Guard.Slipped)}");
    return 1;
}
return 0;

static async Task RunTailorAsync(ModelRunner runner, string model, IReadOnlyList<TailoringItem> items, EvalRun run)
{
    var stats = new CallStats();
    var perItem = new (int Edits, int Flagged, List<string> Slips, bool Failed)[items.Count];
    await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, ct) =>
    {
        var item = items[i];
        var ctx = TailoringEval.Context(item);
        var (job, targets) = TailoringEval.Inputs(item, TailoringEval.Missing(item));
        var input = ResumeTailorTask.BuildInput(job, ctx.Model, targets, item.ConfirmedSkills, ctx.Rules);
        var outcome = await runner.RunAsync(ResumeTailorTask.For(ctx), input, model, 4000, ct);
        stats.Add(outcome.Valid, outcome.ValidFirstTry, outcome.CountedTokens, outcome.LatencyMs, outcome.Error);
        if (outcome.Value is null)
        {
            perItem[i] = (0, 0, [], true);
            return;
        }
        var edits = Tailorly.Docx.Tailoring.EditValidator.Check(ResumeTailorTask.Proposed(outcome.Value), ctx);
        var slips = edits.SelectMany(e => TailoringEval.UnflaggedFacts(e, item).Select(f => $"{item.Id}/{e.ParagraphId}: {f}")).ToList();
        perItem[i] = (edits.Count, edits.Count(e => e.Flags.Contains(Tailorly.Docx.Tailoring.EditFlags.NewFact)), slips, false);
    });
    var editsTotal = perItem.Sum(p => p.Edits);
    var unflagged = perItem.Sum(p => p.Slips.Count);
    run.Tailor = new TailorMetrics(items.Count, perItem.Count(p => !p.Failed), perItem.Count(p => p.Failed), editsTotal,
        perItem.Sum(p => p.Flagged), unflagged, perItem.SelectMany(p => p.Slips).ToList(),
        editsTotal == 0 ? null : Math.Round(unflagged / (double)editsTotal, 3));
    run.TailorCalls = stats.ToSummary();
}

static async Task RunEligibilityAsync(ModelRunner runner, string model, IReadOnlyList<EligibilityItem> items, EvalRun run)
{
    var results = new (EligibilityItem, string?)[items.Count];
    var raw = new (EligibilityItem, string?)[items.Count];
    var stats = new CallStats();
    await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, ct) =>
    {
        var item = items[i];
        var hint = EligibilityRules.Classify(item.Countries, item.LocationText, item.Title, item.Description);
        var input = JobEnrichmentTask.BuildInput(new EnrichmentJobInput(item.Title, item.Company, item.LocationText, item.WorkMode,
            item.EmploymentType, item.Countries, item.Description), hint);
        var outcome = await runner.RunAsync(JobEnrichmentTask.Definition, input, model, 1500, ct);
        stats.Add(outcome.Valid, outcome.ValidFirstTry, outcome.CountedTokens, outcome.LatencyMs, outcome.Error);
        raw[i] = (item, outcome.Value?.Eligibility);
        // What the app stores: hard rules win over the model (SDD §8.6).
        results[i] = (item, outcome.Value is { } v
            ? EligibilityResolver.Resolve(hint, v.Eligibility, JobEnrichmentTask.QuotedEvidence(v.EligibilityEvidence,
                $"{item.Title}\n{item.LocationText}\n{item.Description}")).Eligibility
            : null);
    });
    run.Eligibility = Metrics.ForEligibility(results);
    run.EligibilityModelOnly = Metrics.ForEligibility(raw);
    run.EnrichmentCalls = stats.ToSummary();
}

static async Task RunFitAsync(ModelRunner runner, string model, FitDataset data, IReadOnlyList<FitItem> items, EvalRun run)
{
    var results = new (FitItem, int?)[items.Count];
    var stats = new CallStats();
    var summaries = data.Candidates.ToDictionary(c => c.Key,
        c => JobFitScoreTask.CandidateSummary(c.Value.Profile, c.Value.ConfirmedSkills, c.Value.Preferences));
    await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (i, ct) =>
    {
        var item = items[i];
        var j = item.Job;
        var facts = new FitJobFacts(j.Title, j.Company, j.LocationText, j.WorkMode, j.EmploymentType, j.Eligibility,
            j.DescriptionCompleteness, j.Enrichment);
        var outcome = await runner.RunAsync(JobFitScoreTask.Definition, JobFitScoreTask.BuildInput(summaries[item.CandidateId], facts),
            model, 600, ct);
        stats.Add(outcome.Valid, outcome.ValidFirstTry, outcome.CountedTokens, outcome.LatencyMs, outcome.Error);
        results[i] = (item, outcome.Value?.FitScore);
    });
    run.Fit = Metrics.ForFit(results);
    run.FitCalls = stats.ToSummary();
}

namespace Tailorly.Eval
{
    internal sealed class EvalOptions
    {
        public List<string> Models { get; } = [];
        public HashSet<string> Tasks { get; } = ["eligibility", "fit", "tailor"];
        public int Limit { get; private set; } = int.MaxValue;
        public string? Effort { get; private set; } = "low";
        public string DatasetsDir { get; private set; } = Path.Combine(RepoRoot(), "tests", "Tailorly.Eval", "datasets");
        public string OutDir { get; private set; } = Path.Combine(RepoRoot(), "tests", "Tailorly.Eval", "reports");
        public bool Write { get; private set; } = true;
        public string? Error { get; private set; }

        public static EvalOptions Parse(string[] args)
        {
            var o = new EvalOptions();
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                try
                {
                    switch (args[i])
                    {
                        case "--models": o.Models.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                        case "--tasks":
                            o.Tasks.Clear();
                            foreach (var t in Next().Split(',', StringSplitOptions.TrimEntries)) o.Tasks.Add(t);
                            break;
                        case "--limit": o.Limit = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                        case "--effort": o.Effort = Next(); break;
                        case "--datasets": o.DatasetsDir = Path.GetFullPath(Next()); break;
                        case "--out": o.OutDir = Path.GetFullPath(Next()); break;
                        case "--no-write": o.Write = false; break;
                        default: o.Error = $"Unknown option {args[i]}"; break;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or FormatException)
                {
                    o.Error = ex.Message;
                }
            }
            if (o.Tasks.Except(["eligibility", "fit", "tailor"]).Any()) o.Error = "--tasks accepts eligibility, fit and tailor";
            return o;
        }

        private static string RepoRoot()
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Tailorly.sln"))) return d.FullName;
            return Directory.GetCurrentDirectory();
        }
    }

    /// <param name="Errors">Validation messages of failed replies (content-free, e.g. "reasons[0] has more than 20 words.").</param>
    public sealed record CallSummary(int Calls, double JsonValidFirstTry, double JsonValidAfterRepair, long CountedTokens, int MedianLatencyMs,
        IReadOnlyList<string> Errors);

    internal sealed class CallStats
    {
        private readonly List<(bool Valid, bool First, long Tokens, int Ms, string? Error)> _calls = [];

        public void Add(bool valid, bool first, long tokens, int ms, string? error = null)
        {
            lock (_calls) _calls.Add((valid, first, tokens, ms, error));
        }

        public CallSummary ToSummary()
        {
            if (_calls.Count == 0) return new(0, 0, 0, 0, 0, []);
            var latencies = _calls.Select(c => c.Ms).Order().ToList();
            return new(_calls.Count, Math.Round(_calls.Count(c => c.First) / (double)_calls.Count, 3),
                Math.Round(_calls.Count(c => c.Valid) / (double)_calls.Count, 3), _calls.Sum(c => c.Tokens), latencies[latencies.Count / 2],
                _calls.Where(c => c.Error is not null).Select(c => c.Error!).ToList());
        }
    }

    public sealed class EvalRun
    {
        public required string Model { get; init; }
        public EligibilityMetrics? Eligibility { get; set; }
        public EligibilityMetrics? EligibilityModelOnly { get; set; }
        public CallSummary? EnrichmentCalls { get; set; }
        public FitMetrics? Fit { get; set; }
        public CallSummary? FitCalls { get; set; }
        public TailorMetrics? Tailor { get; set; }
        public CallSummary? TailorCalls { get; set; }
    }

    public sealed class EvalReport
    {
        public DateTimeOffset RunAt { get; init; }
        public required Dictionary<string, string> PromptVersions { get; init; }
        public int EligibilityItems { get; init; }
        public int FitItems { get; init; }
        public int TailoringItems { get; init; }
        public required GuardMetrics Guard { get; init; }
        public List<EvalRun> Runs { get; } = [];

        /// <summary>The tailoring sections: the guard on hand-written edits, then each model's hallucination check.</summary>
        private void TailoringMarkdown(StringBuilder sb)
        {
            sb.AppendLine().AppendLine("## Tailoring hallucination check (target: 0 unflagged new facts)").AppendLine()
              .AppendLine(CultureInfo.InvariantCulture,
                  $"Guard on hand-written edits: {Guard.Adversarial} adversarial → {Guard.Refused} refused, {Guard.Flagged} flagged new_fact, " +
                  $"**{Guard.Slipped.Count} slipped**; {Guard.Benign} benign → {Guard.Benign - Guard.FalsePositives.Count} passed, " +
                  $"{Guard.FalsePositives.Count} false positives.");
            foreach (var s in Guard.Slipped) sb.AppendLine(CultureInfo.InvariantCulture, $"- slipped: {s}");
            foreach (var s in Guard.FalsePositives) sb.AppendLine(CultureInfo.InvariantCulture, $"- false positive: {s}");
            var runs = Runs.Where(r => r.Tailor is not null).ToList();
            if (runs.Count == 0) return;
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Model runs on {TailoringItems} cases:").AppendLine()
              .AppendLine("| Model | Completed / failed | Edits | Flagged new_fact | Unflagged new facts (target 0) | Rate | JSON valid 1st / after repair | Tokens |")
              .AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var r in runs)
            {
                var t = r.Tailor!;
                var c = r.TailorCalls;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {r.Model} | {t.Completed} / {t.Failed} | {t.Edits} | {t.FlaggedNewFacts} | **{t.UnflaggedNewFacts}** | {F(t.NewFactRate)} | " +
                    $"{(c is null ? "–" : $"{c.JsonValidFirstTry:0.###} / {c.JsonValidAfterRepair:0.###}")} | {c?.CountedTokens ?? 0} |");
            }
            foreach (var r in runs.Where(r => r.Tailor!.Slips.Count > 0 || r.TailorCalls is { Errors.Count: > 0 }))
            {
                sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"### {r.Model}: tailoring details");
                foreach (var s in r.Tailor!.Slips) sb.AppendLine(CultureInfo.InvariantCulture, $"- unflagged: {s}");
                foreach (var e in r.TailorCalls?.Errors ?? []) sb.AppendLine(CultureInfo.InvariantCulture, $"- resume_tailor: {e}");
            }
        }

        /// <summary>Targets from PRD §14.3: eligibility ≥ 90 % (unclear allowed), JSON validity ≥ 99 %, Spearman for fit.</summary>
        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"# Tailorly.Eval report {RunAt:yyyy-MM-dd HH:mm} UTC").AppendLine()
              .AppendLine(CultureInfo.InvariantCulture, $"Prompts: {string.Join(", ", PromptVersions.Values)}. Items: {EligibilityItems} eligibility, {FitItems} fit.")
              .AppendLine()
              .AppendLine("| Model | Elig. accuracy | Unclear-tolerant (target ≥ 0.90) | Critical errors | JSON valid 1st / after repair | Fit Spearman | Fit MAE | Fit ±1 | Tokens |")
              .AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var r in Runs)
            {
                var calls = new[] { r.EnrichmentCalls, r.FitCalls }.Where(c => c is { Calls: > 0 }).ToList();
                var valid = calls.Count == 0 ? "–" : string.Join(" · ", calls.Select(c => $"{c!.JsonValidFirstTry:0.###} / {c.JsonValidAfterRepair:0.###}"));
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {r.Model} | {F(r.Eligibility?.Accuracy)} | {F(r.Eligibility?.UnclearTolerantAccuracy)} | {r.Eligibility?.CriticalErrors.ToString(CultureInfo.InvariantCulture) ?? "–"} | {valid} | {F(r.Fit?.Spearman)} | {F(r.Fit?.MeanAbsoluteError)} | {F(r.Fit?.WithinOne)} | {calls.Sum(c => c!.CountedTokens)} |");
            }
            foreach (var r in Runs.Where(r => r.Eligibility is { Misses.Count: > 0 } || r.Fit is { Misses.Count: > 0 }))
            {
                sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## {r.Model}: disagreements");
                foreach (var m in (r.Eligibility?.Misses ?? []).Take(30)) sb.AppendLine(CultureInfo.InvariantCulture, $"- eligibility {m}");
                foreach (var m in (r.Fit?.Misses ?? []).Take(30)) sb.AppendLine(CultureInfo.InvariantCulture, $"- fit {m}");
            }
            foreach (var r in Runs.Where(r => r.EnrichmentCalls is { Errors.Count: > 0 } || r.FitCalls is { Errors.Count: > 0 }))
            {
                sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## {r.Model}: validation problems (repaired or failed)");
                foreach (var e in (r.EnrichmentCalls?.Errors ?? []).Take(20)) sb.AppendLine(CultureInfo.InvariantCulture, $"- job_enrichment: {e}");
                foreach (var e in (r.FitCalls?.Errors ?? []).Take(20)) sb.AppendLine(CultureInfo.InvariantCulture, $"- job_fit_score: {e}");
            }
            TailoringMarkdown(sb);
            return sb.ToString();
        }

        private static string F(double? v) => v is { } x ? x.ToString("0.###", CultureInfo.InvariantCulture) : "–";
    }
}
