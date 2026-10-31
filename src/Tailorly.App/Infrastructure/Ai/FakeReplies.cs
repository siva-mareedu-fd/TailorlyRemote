using System.Text.Json;
using System.Text.RegularExpressions;
using Tailorly.App.Ai;
using Tailorly.Core.Ports;

namespace Tailorly.App.Infrastructure.Ai;

/// <summary>
/// Replies the offline provider builds from the request for tasks whose answer must refer to the request itself (paragraph
/// ids of the uploaded résumé, the job's company), so local development without an API key can run tailoring end to end.
/// They are deliberately modest: requirements are judged by plain text search, edits only adjust punctuation, and the
/// letter uses nothing but the job's title and company and the candidate's name.
/// </summary>
internal static partial class FakeReplies
{
    public static string? For(ModelRequest request)
    {
        var data = request.Messages.Count > 0 ? request.Messages[0].Text : "";
        var cached = request.CacheablePrefix ?? "";
        return request.Task switch
        {
            AiTaskNames.AtsAnalysis => Analysis(data, cached),
            AiTaskNames.ResumeTailor => Tailor(cached),
            AiTaskNames.CoverLetter => Letter(data, cached),
            _ => null,
        };
    }

    private static string Analysis(string job, string resume)
    {
        var paragraphs = ResumeLine().Matches(resume).Select(m => (Id: m.Groups["id"].Value, Text: m.Groups["text"].Value)).ToList();
        var skills = Field(job, "Required skills").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s != "not stated").Take(6).ToList();
        if (skills.Count == 0) skills = ["Relevant experience"];
        var requirements = skills.Select(s =>
        {
            var evidence = paragraphs.FirstOrDefault(p => p.Text.Contains(s, StringComparison.OrdinalIgnoreCase)).Id;
            return new
            {
                text = s,
                type = "required",
                status = evidence is null ? "missing" : "met",
                evidence_paragraph_ids = evidence is null ? Array.Empty<string>() : [evidence],
            };
        }).ToList();
        return JsonSerializer.Serialize(new
        {
            requirements,
            title_seniority_label = 0.7,
            experience_relevance = new { score = 0.7, justification = "Offline fake provider: a fixed estimate." },
            gaps = requirements.Where(r => r.status == "missing").Select(r => new { skill = r.text, likely_has_based_on_profile = false }),
        });
    }

    /// <summary>Up to three editable paragraphs get their final full stop added or removed.</summary>
    private static string Tailor(string resume)
    {
        var edits = TailorLine().Matches(resume)
            .Where(m => m.Groups["kind"].Value == "editable")
            .Take(3)
            .Select(m =>
            {
                var text = m.Groups["text"].Value.Trim();
                return new
                {
                    paragraph_id = m.Groups["id"].Value,
                    new_text = text.EndsWith('.') ? text[..^1] : text + ".",
                    rationale = "Offline fake provider: punctuation only.",
                    uses_confirmed_fact = false,
                };
            });
        return JsonSerializer.Serialize(new { edits, unaddressed_gaps = Array.Empty<string>() });
    }

    private static string Letter(string data, string candidate)
    {
        var title = Field(data, "Title");
        var company = Field(data, "Company");
        var name = Field(candidate, "Name");
        var tone = Field(data, "Tone") is "confident" ? "confident" : "concise";
        string[] paragraphs =
        [
            $"Hello {company} team,",
            $"I would like to apply for the {title} role.",
            "My résumé sets out the work I have done, and I would be glad to talk it through with you.",
            "Best regards,",
            name == "not stated" ? "Candidate" : name,
        ];
        return JsonSerializer.Serialize(new { paragraphs, word_count = paragraphs.Sum(p => p.Split(' ').Length), tone });
    }

    private static string Field(string text, string name)
    {
        var m = Regex.Match(text, "^" + Regex.Escape(name) + @":[ \t]*(?<v>[^\r\n]*)", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        return m.Success ? m.Groups["v"].Value.Trim() : "";
    }

    [GeneratedRegex(@"^\[(?<id>p\d+)\] \([a-z]+\) (?<text>.*)$", RegexOptions.Multiline, 1000)]
    private static partial Regex ResumeLine();

    [GeneratedRegex(@"^(?<id>p\d+) \| [a-z]+ \| (?<kind>editable|locked) \| (?:prefix ""[^""]*"" \| )?(?<text>.*)$", RegexOptions.Multiline, 1000)]
    private static partial Regex TailorLine();
}
