using System.Globalization;
using System.Net;
using System.Text;
using Tailorly.App.Features.Jobs;
using Tailorly.Core.Jobs;
using Tailorly.Core.Ports;

namespace Tailorly.App.Features.Digests;

/// <param name="Jobs">Ranked items to list (already limited to <c>Digest:EmailMaxItems</c>).</param>
/// <param name="TotalJobs">All items the email covers (more may be in the app).</param>
/// <param name="DegradedReason">Why fit scores are missing: <c>budget</c>, <c>ai</c>, or null when not degraded.</param>
/// <param name="ResetsOn">Discovery budget reset date (for the budget notice).</param>
/// <param name="ScheduleText">E.g. "18:00 Asia/Kolkata".</param>
public sealed record DigestEmailModel(
    string To,
    string? Name,
    DateOnly LocalDate,
    IReadOnlyList<JobSummaryDto> Jobs,
    int TotalJobs,
    string? DegradedReason,
    string? ResetsOn,
    string ScheduleText,
    string ShortlistUrl,
    Func<Guid, string> JobUrl,
    string PreferencesUrl,
    string UnsubscribeUrl);

/// <summary>
/// The daily digest email (PRD F7): a ranked list where each item has title, company, location and eligibility badge,
/// salary label, fit score, a two-line reason, and the links Open in app, Apply (the source-supplied apply URL) and
/// "via {Source}" (a normal, followed link to the listing, PRD F14). Every job and user value is HTML-encoded; links are
/// only rendered for http(s) URLs. Carries the RFC 8058 one-click unsubscribe URL for the List-Unsubscribe headers.
/// </summary>
public static class DigestEmailTemplate
{
    private const string Text = "#16150f";
    private const string Muted = "#6a675c";
    private const string Accent = "#0e7a53";
    private const string AccentSoft = "#e3f1ea";
    private const string Amber = "#8a5a00";
    private const string AmberSoft = "#fbf0d9";
    private const string Border = "#e6e3da";

    public static EmailMessage Build(DigestEmailModel m)
    {
        var n = m.TotalJobs;
        var subject = n == 0
            ? "No new matches today · Tailorly Remote"
            : $"{n} new remote {(n == 1 ? "match" : "matches")} for you · Tailorly Remote";
        return new EmailMessage(m.To, subject, Html(m), PlainText(m), m.UnsubscribeUrl);
    }

    private static string Html(DigestEmailModel m)
    {
        var e = (Func<string?, string>)(s => WebUtility.HtmlEncode(s ?? ""));
        var sb = new StringBuilder();
        sb.Append($"""<!doctype html><html><body style="margin:0;padding:24px;background:#faf9f6;font-family:Arial,sans-serif;color:{Text};line-height:1.5">""")
          .Append("""<div style="max-width:640px;margin:0 auto">""")
          .Append($"""<p style="margin:0 0 4px">{e(Greeting(m.Name))}</p>""")
          .Append($"""<h1 style="font-size:20px;margin:0 0 4px">Today’s shortlist</h1>""")
          .Append($"""<p style="margin:0 0 16px;color:{Muted};font-size:13px">{e(DateText(m.LocalDate))} · {e(Intro(m))}</p>""");

        if (Notice(m) is { } notice)
            sb.Append($"""<p style="background:{AmberSoft};color:{Amber};padding:10px 12px;border-radius:8px;font-size:13px">{e(notice)}</p>""");

        foreach (var j in m.Jobs)
        {
            sb.Append($"""<div style="border-top:1px solid {Border};padding:14px 0">""")
              .Append($"""<div style="font-size:16px;font-weight:bold"><a href="{e(m.JobUrl(j.Id))}" style="color:{Text};text-decoration:none">{e(j.Title)}</a></div>""")
              .Append($"""<div style="color:{Muted};font-size:13px">{e(j.CompanyName)}{(j.LocationText is { Length: > 0 } loc ? " · " + e(loc) : "")}</div>""")
              .Append("""<div style="margin:6px 0">""")
              .Append(Badge(j.Score is { } s ? $"Fit {s.FitScore}/10" : "Not scored", j.Score is { FitScore: >= 9 }))
              .Append(Badge(EligibilityText(j.Eligibility), j.Eligibility != Eligibility.Unclear, warn: j.Eligibility == Eligibility.Unclear))
              .Append(Badge(SalaryText(j.Salary, j.SalaryLabel), false))
              .Append("</div>");
            if (Reasons(j) is { Length: > 0 } reasons)
                sb.Append($"""<div style="font-size:13px">{e(reasons)}</div>""");
            sb.Append($"""<div style="font-size:13px;margin-top:6px"><a href="{e(m.JobUrl(j.Id))}" style="color:{Accent}">Open in app</a>""");
            if (IsHttp(j.ApplyUrl))
                sb.Append($""" · <a href="{e(j.ApplyUrl)}" style="color:{Accent}">Apply on {e(Host(j.ApplyUrl))}</a>""");
            if (IsHttp(j.Source.SourceUrl))
                sb.Append($""" · <a href="{e(j.Source.SourceUrl)}" style="color:{Muted}">via {e(j.Source.Name)}</a>""");
            else
                sb.Append($""" · <span style="color:{Muted}">via {e(j.Source.Name)}</span>""");
            sb.Append("</div></div>");
        }

        if (m.TotalJobs > m.Jobs.Count)
            sb.Append($"""<p style="font-size:13px"><a href="{e(m.ShortlistUrl)}" style="color:{Accent}">See all {m.TotalJobs} matches in the app</a></p>""");
        else if (m.Jobs.Count > 0)
            sb.Append($"""<p style="font-size:13px"><a href="{e(m.ShortlistUrl)}" style="color:{Accent}">Open your shortlist</a></p>""");

        sb.Append($"""<p style="border-top:1px solid {Border};padding-top:12px;color:{Muted};font-size:12px">""")
          .Append($"You get this email because your daily digest is on ({e(m.ScheduleText)}). Job listings are from the sources named on each job. ")
          .Append($"""<a href="{e(m.PreferencesUrl)}" style="color:{Muted}">Change preferences</a> · """)
          .Append($"""<a href="{e(m.UnsubscribeUrl)}" style="color:{Muted}">Turn off the daily digest</a></p>""")
          .Append($"""<p style="color:{Muted};font-size:12px">Tailorly Remote</p></div></body></html>""");
        return sb.ToString();
    }

    private static string PlainText(DigestEmailModel m)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Greeting(m.Name)).AppendLine()
          .AppendLine($"Today’s shortlist · {DateText(m.LocalDate)}")
          .AppendLine(Intro(m)).AppendLine();
        if (Notice(m) is { } notice) sb.AppendLine(notice).AppendLine();
        var rank = 0;
        foreach (var j in m.Jobs)
        {
            rank++;
            sb.AppendLine($"{rank}. {j.Title}")
              .AppendLine($"   {j.CompanyName}{(j.LocationText is { Length: > 0 } loc ? " · " + loc : "")}")
              .AppendLine($"   {(j.Score is { } s ? $"Fit {s.FitScore}/10" : "Not scored")} · {EligibilityText(j.Eligibility)} · {SalaryText(j.Salary, j.SalaryLabel)}");
            if (Reasons(j) is { Length: > 0 } reasons) sb.AppendLine($"   {reasons}");
            sb.AppendLine($"   Open in app: {m.JobUrl(j.Id)}");
            if (IsHttp(j.ApplyUrl)) sb.AppendLine($"   Apply: {j.ApplyUrl}");
            sb.AppendLine($"   via {j.Source.Name}: {j.Source.SourceUrl}").AppendLine();
        }
        if (m.Jobs.Count > 0) sb.AppendLine($"Your shortlist: {m.ShortlistUrl}").AppendLine();
        sb.AppendLine($"You get this email because your daily digest is on ({m.ScheduleText}).")
          .AppendLine($"Change preferences: {m.PreferencesUrl}")
          .AppendLine($"Turn off the daily digest: {m.UnsubscribeUrl}")
          .AppendLine().AppendLine("Tailorly Remote");
        return sb.ToString();
    }

    private static string Greeting(string? name) => string.IsNullOrWhiteSpace(name) ? "Hello," : $"Hello {name},";

    private static string DateText(DateOnly d) => d.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Intro(DigestEmailModel m) => m.TotalJobs switch
    {
        0 => "No new jobs matched your preferences since your last digest.",
        1 => "1 new job matched your preferences.",
        var n => $"{n} new jobs matched your preferences, best fit first.",
    };

    private static string? Notice(DigestEmailModel m) => m.DegradedReason switch
    {
        "budget" => $"Your Discovery budget for this month is used up{(m.ResetsOn is null ? "" : $" until {m.ResetsOn}")}, so these jobs are ranked by title and skills only, without fit scores.",
        "ai" => "Our AI service was unavailable today, so these jobs are ranked by title and skills only, without fit scores.",
        _ => null,
    };

    private static string Badge(string text, bool positive, bool warn = false)
    {
        var (fg, bg) = warn ? (Amber, AmberSoft) : positive ? (Accent, AccentSoft) : (Muted, "#f1efe9");
        return $"""<span style="display:inline-block;font-family:monospace;font-size:11px;padding:1px 7px;margin:0 4px 4px 0;border-radius:4px;color:{fg};background:{bg}">{WebUtility.HtmlEncode(text)}</span>""";
    }

    private static string Reasons(JobSummaryDto j) =>
        j.Score is { Reasons.Count: > 0 } s ? string.Join(" · ", s.Reasons.Take(2)) : "";

    internal static string EligibilityText(string eligibility) => eligibility switch
    {
        Eligibility.Worldwide => "Worldwide",
        Eligibility.IndiaEligible => "India-eligible",
        Eligibility.Restricted => "Restricted",
        _ => "Eligibility unclear ⚠",
    };

    /// <summary>"$60k–$90k (stated) ≈ ₹50.4L–₹75.6L", "₹30L–₹45L (estimated)" or "Salary not listed".</summary>
    internal static string SalaryText(SalaryDto s, string label)
    {
        if (label == "not_listed" || (s.Min is null && s.Max is null)) return "Salary not listed";
        var text = Range(s.Min, s.Max, s.Currency) + $" ({label})";
        if (s.ApproxInr is { } inr && (inr.Min is not null || inr.Max is not null)) text += " ≈ " + Range(inr.Min, inr.Max, "INR");
        return text;
    }

    private static string Range(long? min, long? max, string? currency)
    {
        var lo = min ?? max;
        var hi = max ?? min;
        return lo == hi ? Money(lo!.Value, currency) : $"{Money(lo!.Value, currency)}–{Money(hi!.Value, currency)}";
    }

    internal static string Money(long amount, string? currency)
    {
        var symbol = currency switch { "USD" => "$", "EUR" => "€", "GBP" => "£", "INR" => "₹", null => "", var c => c + " " };
        if (currency == "INR")
        {
            return amount >= 10_000_000
                ? $"{symbol}{(amount / 10_000_000m).ToString("0.##", CultureInfo.InvariantCulture)}Cr"
                : $"{symbol}{(amount / 100_000m).ToString("0.#", CultureInfo.InvariantCulture)}L";
        }
        return amount >= 1000
            ? $"{symbol}{(amount / 1000m).ToString("0.#", CultureInfo.InvariantCulture)}k"
            : $"{symbol}{amount.ToString(CultureInfo.InvariantCulture)}";
    }

    private static bool IsHttp(string? url) => ScoredJobs.IsHttp(url);

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "the employer site";
}
