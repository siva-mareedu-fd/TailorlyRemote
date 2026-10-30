using Tailorly.Core.Jobs;

namespace Tailorly.Eval;

/// <summary>Eligibility results for one model (or the rules baseline).</summary>
/// <param name="Accuracy">Exact matches / answered items.</param>
/// <param name="UnclearTolerantAccuracy">Exact matches plus "unclear" answers (PRD §14.3: "unclear allowed").</param>
/// <param name="CriticalErrors">Said open to India (worldwide/india_eligible) when the label is restricted.</param>
/// <param name="Confusion">label → predicted → count.</param>
public sealed record EligibilityMetrics(
    int Items,
    int Answered,
    double Accuracy,
    double UnclearTolerantAccuracy,
    int CriticalErrors,
    IReadOnlyDictionary<string, Dictionary<string, int>> Confusion,
    IReadOnlyList<string> Misses);

/// <summary>Fit-score agreement with human labels.</summary>
public sealed record FitMetrics(int Items, int Answered, double? Spearman, double MeanAbsoluteError, double WithinOne,
    IReadOnlyList<string> Misses);

public static class Metrics
{
    private static readonly string[] Classes = [Eligibility.Worldwide, Eligibility.IndiaEligible, Eligibility.Restricted, Eligibility.Unclear];

    public static EligibilityMetrics ForEligibility(IReadOnlyList<(EligibilityItem Item, string? Predicted)> results)
    {
        var confusion = Classes.ToDictionary(c => c, _ => Classes.ToDictionary(p => p, _ => 0));
        var answered = results.Where(r => r.Predicted is not null).ToList();
        foreach (var (item, predicted) in answered) confusion[item.Label][predicted!]++;
        var exact = answered.Count(r => r.Predicted == r.Item.Label);
        var tolerant = answered.Count(r => r.Predicted == r.Item.Label || r.Predicted == Eligibility.Unclear);
        var critical = answered.Count(r => r.Item.Label == Eligibility.Restricted && Eligibility.OpenToIndia(r.Predicted!));
        var misses = answered.Where(r => r.Predicted != r.Item.Label).Select(r => $"{r.Item.Id}: {r.Item.Label} → {r.Predicted}").ToList();
        return new(results.Count, answered.Count, Ratio(exact, answered.Count), Ratio(tolerant, answered.Count), critical, confusion, misses);
    }

    public static FitMetrics ForFit(IReadOnlyList<(FitItem Item, int? Predicted)> results)
    {
        var answered = results.Where(r => r.Predicted is not null).ToList();
        if (answered.Count == 0) return new(results.Count, 0, null, 0, 0, []);
        var labels = answered.Select(r => (double)r.Item.Label).ToArray();
        var predicted = answered.Select(r => (double)r.Predicted!.Value).ToArray();
        var mae = answered.Average(r => Math.Abs(r.Item.Label - r.Predicted!.Value));
        var within = Ratio(answered.Count(r => Math.Abs(r.Item.Label - r.Predicted!.Value) <= 1), answered.Count);
        var misses = answered.Where(r => Math.Abs(r.Item.Label - r.Predicted!.Value) > 1)
            .Select(r => $"{r.Item.Id}: {r.Item.Label} → {r.Predicted}").ToList();
        return new(results.Count, answered.Count, Spearman(labels, predicted), Math.Round(mae, 2), within, misses);
    }

    /// <summary>Spearman's rank correlation (average ranks for ties); null when either side has no variance.</summary>
    public static double? Spearman(double[] a, double[] b)
    {
        if (a.Length != b.Length || a.Length < 2) return null;
        var ra = Ranks(a);
        var rb = Ranks(b);
        var ma = ra.Average();
        var mb = rb.Average();
        double cov = 0, va = 0, vb = 0;
        for (var i = 0; i < ra.Length; i++)
        {
            cov += (ra[i] - ma) * (rb[i] - mb);
            va += (ra[i] - ma) * (ra[i] - ma);
            vb += (rb[i] - mb) * (rb[i] - mb);
        }
        return va == 0 || vb == 0 ? null : Math.Round(cov / Math.Sqrt(va * vb), 3);
    }

    private static double[] Ranks(double[] values)
    {
        var order = values.Select((v, i) => (v, i)).OrderBy(x => x.v).ToArray();
        var ranks = new double[values.Length];
        for (var i = 0; i < order.Length;)
        {
            var j = i;
            while (j + 1 < order.Length && order[j + 1].v == order[i].v) j++;
            var avg = (i + j) / 2.0 + 1;
            for (var k = i; k <= j; k++) ranks[order[k].i] = avg;
            i = j + 1;
        }
        return ranks;
    }

    private static double Ratio(int n, int d) => d == 0 ? 0 : Math.Round(n / (double)d, 3);
}
