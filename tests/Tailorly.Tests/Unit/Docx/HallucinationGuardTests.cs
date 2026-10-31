using FluentAssertions;
using Tailorly.Docx;
using Tailorly.Docx.Tailoring;
using Tailorly.Tests.Infrastructure;

namespace Tailorly.Tests.Unit.Docx;

/// <summary>
/// The hallucination test set (PRD §8.3 step 5, §14.1 "edit validators"): edits that add a number, an employer, a title,
/// a degree or a skill the user never stated must be refused (a violation the model has to fix) or flagged
/// <c>new_fact</c> (rejected by default, accepted only once the user confirms the term). Rewordings that add no fact must
/// pass untouched. The résumé is <see cref="DocxBuilder.SampleResume"/>; the job asks for Kubernetes, Kafka, AWS, Terraform
/// and Go besides the résumé's own stack.
/// </summary>
public sealed class HallucinationGuardTests
{
    private static readonly DocModel Model = ResumeExtractor.Extract(new MemoryStream(DocxBuilder.SampleResume()));

    private static readonly SkillGroup[] Job =
    [
        new("C#", []), new(".NET", ["dotnet"]), new("PostgreSQL", ["Postgres"]), new("Kubernetes", ["k8s"]), new("Kafka", []),
        new("AWS", ["Amazon Web Services"]), new("Terraform", []), new("Go", ["Golang"]),
    ];

    private static TailorContext Context(params string[] confirmed) =>
        new(Model, FactSource.Build(Model.Paragraphs.Select(p => p.Text).Concat(Model.HeaderFooterLines).Concat(confirmed)), Job,
            new TailorRules());

    private static string Id(string startsWith) => Model.Paragraphs.Single(p => p.Text.StartsWith(startsWith, StringComparison.Ordinal)).Id;

    private static (IReadOnlyList<string> Violations, IReadOnlyList<CheckedEdit> Checked) Run(string paragraph, string newText, params string[] confirmed)
    {
        var ctx = Context(confirmed);
        var edits = new[] { new ProposedEdit(Id(paragraph), newText, "test", false) };
        var v = EditValidator.Violations(edits, ctx);
        return (v, v.Count == 0 ? EditValidator.Check(edits, ctx) : []);
    }

    /// <summary>Each of these adds a fact the résumé does not state; none may pass as a safe edit.</summary>
    public static TheoryData<string, string, string> Hallucinations() => new()
    {
        // numbers, amounts and metrics (refused)
        { "Built REST", "Built REST APIs serving 2 million requests a day using C# and PostgreSQL.", "number" },
        { "Reduced settlement", "Reduced settlement latency by 45% by redesigning the nightly batch pipeline.", "percent" },
        { "Maintained billing", "Maintained billing services handling $3M a month and wrote integration tests.", "currency" },
        { "Backend engineer", "Backend engineer with 8 years of experience building .NET services for payments.", "years" },
        { "Maintained billing", "Maintained billing services and wrote integration tests for ten releases.", "spelled number" },
        { "Built REST", "Built REST APIs 3x faster for the payments team using C# and PostgreSQL.", "multiplier" },
        { "Built REST", "Led a team of 12 engineers building REST APIs in C# and PostgreSQL.", "team size" },
        { "Backend engineer", "Backend engineer building .NET services for payments since 2015.", "date" },
        { "Reduced settlement", "Reduced settlement latency by twenty-five percent with a batch redesign.", "spelled percent" },
        // employers and organisations (new_fact)
        { "Maintained billing", "Maintained billing services at Google and wrote integration tests for every release.", "employer" },
        { "Built REST", "Built REST APIs for Acme Cloud's payments team using C# and PostgreSQL.", "target company" },
        { "Maintained billing", "Maintained billing services for Microsoft Teams and wrote integration tests.", "product" },
        // titles and seniority (new_fact)
        { "Backend engineer", "Principal backend engineer with 6 years of experience building .NET services.", "title" },
        { "Backend engineer", "Backend engineer and team lead with 6 years of experience building .NET services.", "lower-case title" },
        { "Backend engineer", "Backend architect with 6 years of experience building .NET services for payments.", "architect" },
        // degrees and certifications (new_fact)
        { "Backend engineer", "Backend engineer with a Master's degree and 6 years of experience building .NET services.", "degree" },
        { "Maintained billing", "Maintained billing services as an AWS Certified Solutions Architect.", "certification" },
        // skills (new_fact)
        { "Platforms:", "Platforms: .NET, PostgreSQL, Docker, Kubernetes", "job skill in a skills line" },
        { "Built REST", "Built REST APIs for the payments team using C#, PostgreSQL and kafka.", "lower-case job skill" },
        { "Built REST", "Built REST APIs for the payments team using C# and PostgreSQL, deployed on k8s.", "job skill alias" },
        { "Languages:", "Languages: C#, SQL, TypeScript, Go", "short job skill" },
        { "Languages:", "Languages: C#, SQL, TypeScript, Rust", "skill at the end of a list" },
        { "Languages:", "Languages: Rust, C#, SQL, TypeScript", "skill first in a list" },
        { "Maintained billing", "Maintained billing services with Terraform and wrote integration tests.", "infrastructure tool" },
        { "Backend engineer", "Backend engineer with 6 years of experience building .NET and Java services.", "language" },
        { "Maintained billing", "Maintained billing services and wrote python scripts for every release.", "lower-case language" },
        { "Built REST", "Built GraphQL and REST APIs for the payments team using C# and PostgreSQL.", "mixed-case tech" },
        { "Built REST", "Built REST APIs on AWS Lambda for the payments team using C# and PostgreSQL.", "cloud service" },
        { "Built REST", "Built REST APIs with OAuth2 for the payments team using C# and PostgreSQL.", "tech with a digit" },
        // links (refused) and handles (new_fact)
        { "Built REST", "Built REST APIs for the payments team, see https://asha.dev for details.", "link" },
        { "Built REST", "Built REST APIs for the payments team, code at github.com/asha-example.", "bare link" },
    };

    [Theory]
    [MemberData(nameof(Hallucinations))]
    public void An_edit_that_adds_a_fact_is_refused_or_flagged(string paragraph, string newText, string kind)
    {
        var (violations, checkedEdits) = Run(paragraph, newText);
        var safe = violations.Count == 0 && checkedEdits.Count == 1 && !checkedEdits[0].Flags.Contains(EditFlags.NewFact);
        safe.Should().BeFalse($"a {kind} the résumé does not state must never pass as a safe edit");
        if (violations.Count == 0) checkedEdits[0].NewTerms.Should().NotBeEmpty("a flagged edit says which terms are new");
    }

    /// <summary>Rewordings using only what the résumé says; none may be refused or flagged as a new fact.</summary>
    public static TheoryData<string, string> Rewordings() => new()
    {
        { "Built REST", "Designed and built REST APIs in C# and PostgreSQL for the payments team." },
        { "Reduced settlement", "Cut settlement latency by 30% through a redesign of the nightly batch pipeline." },
        { "Reduced settlement", "Reduced settlement latency by 30 percent by redesigning the nightly batch pipeline." },
        { "Backend engineer", "Backend engineer with six years of experience building .NET payment services." },
        { "Backend engineer", "Backend engineer with 6 years of experience building .NET services on Microsoft Azure for payments." },
        { "Platforms:", "Platforms: PostgreSQL, Docker, .NET" },
        { "Platforms:", "Platforms: Postgres, Docker, .NET" },
        { "Maintained billing", "Maintained billing services and added integration tests to every release." },
        { "Built REST", "Built REST APIs using C# and PostgreSQL, used daily by the payments team." },
        { "Languages:", "Languages: TypeScript, C#, SQL" },
    };

    [Theory]
    [MemberData(nameof(Rewordings))]
    public void A_rewording_that_adds_no_fact_passes(string paragraph, string newText)
    {
        var (violations, checkedEdits) = Run(paragraph, newText);
        violations.Should().BeEmpty();
        checkedEdits.Should().ContainSingle().Which.Flags.Should().NotContain(EditFlags.NewFact);
    }

    [Fact]
    public void A_confirmed_skill_is_no_longer_a_new_fact()
    {
        Run("Platforms:", "Platforms: .NET, PostgreSQL, Docker, Kubernetes").Checked.Single().NewTerms.Should().Equal("Kubernetes");
        Run("Platforms:", "Platforms: .NET, PostgreSQL, Docker, Kubernetes", "Kubernetes").Checked.Single().Flags.Should().NotContain(EditFlags.NewFact);
        Run("Built REST", "Built REST APIs for the payments team using C# and PostgreSQL, deployed on k8s.", "Kubernetes")
            .Checked.Single().Flags.Should().NotContain(EditFlags.NewFact, "an alias of a confirmed job skill counts as confirmed");
    }

    [Fact]
    public void A_missing_job_skill_claimed_in_other_words_is_flagged()
    {
        var ctx = new TailorContext(Model, Context().Facts,
            [new SkillGroup("LLM fine-tuning", []), new SkillGroup("Performance testing", [])], new TailorRules());
        EditValidator.Check([new ProposedEdit(Id("Built REST"), "Built REST APIs and fine-tuned large language models for payments.", null, false)], ctx)
            .Single().NewTerms.Should().Equal("LLM fine-tuning");
        EditValidator.Check([new ProposedEdit(Id("Maintained billing"), "Maintained billing services and ran performance tests for every release.", null, false)], ctx)
            .Single().NewTerms.Should().Equal("Performance testing");
        EditValidator.Check([new ProposedEdit(Id("Maintained billing"), "Maintained billing services and wrote tests for every release.", null, false)], ctx)
            .Single().Flags.Should().NotContain(EditFlags.NewFact, "the résumé already says it writes tests");
    }

    [Fact]
    public void New_numbers_are_violations_the_model_must_fix_and_terms_are_flags_the_user_decides()
    {
        Run("Reduced settlement", "Reduced settlement latency by 45% by redesigning the nightly batch pipeline.").Violations
            .Should().ContainSingle().Which.Should().Contain("adds a number").And.NotContain("45", "messages carry no content");
        var flagged = Run("Maintained billing", "Maintained billing services at Google and wrote integration tests for every release.");
        flagged.Violations.Should().BeEmpty();
        flagged.Checked.Single().NewTerms.Should().Equal("Google");
    }

    // ---------- the edit-only contract ----------

    [Fact]
    public void Contract_breaks_are_violations()
    {
        var ctx = Context();
        string V(params ProposedEdit[] edits) => string.Join(" | ", EditValidator.Violations(edits, ctx));
        ProposedEdit E(string startsWith, string text) => new(Id(startsWith), text, null, false);

        V(new ProposedEdit("p999", "x", null, false)).Should().Contain("does not exist");
        V(E("Senior Software Engineer", "Staff Software Engineer, Example Corp")).Should().Contain("locked");
        V(E("Built REST", "Built REST APIs."), E("Built REST", "Built APIs.")).Should().Contain("same paragraph again");
        V(E("Built REST", "  ")).Should().Contain("empty");
        V(E("Built REST", "Built REST APIs\nfor payments.")).Should().Contain("line break");
        V(E("Built REST", "**Built** REST APIs for the payments team.")).Should().Contain("markdown");
        V(E("Built REST", "- Built REST APIs for the payments team.")).Should().Contain("markdown");
        V(E("Built REST", "Built REST APIs for the payments team 🚀")).Should().Contain("emoji");
        V(E("Built REST", "I built REST APIs for the payments team using C# and PostgreSQL.")).Should().Contain("first-person");
        V(E("Built REST", "Built REST APIs, email asha@other.example for details.")).Should().Contain("link or email");

        var many = Enumerable.Range(0, 26).Select(_ => E("Built REST", "Built REST APIs.")).ToArray();
        EditValidator.Violations(many, ctx).Should().Contain(m => m.Contains("at most 25", StringComparison.Ordinal));
    }

    [Fact]
    public void The_original_contact_details_and_first_person_style_are_not_violations()
    {
        var ctx = Context();
        EditValidator.Violations([new ProposedEdit(Id("Built REST"), "Built REST APIs; questions to asha@example.test.", null, false)], ctx)
            .Should().BeEmpty("the email is in the résumé");
        new TailorRules(AllowFirstPerson: true).AllowFirstPerson.Should().BeTrue();
        EditValidator.Violations([new ProposedEdit(Id("Built REST"), "I built REST APIs.", null, false)],
            new TailorContext(Model, ctx.Facts, Job, new TailorRules(AllowFirstPerson: true))).Should().BeEmpty();
    }

    // ---------- flags and normalisation ----------

    [Fact]
    public void A_much_longer_edit_is_flagged_length()
    {
        var (_, c) = Run("Built REST", "Built REST APIs for the payments team using C# and PostgreSQL, owning the design, the reviews, " +
                                       "the releases and the support of the services for the payments team.");
        c.Single().Flags.Should().Contain(EditFlags.Length);
        Run("Built REST", "Built REST APIs in C# and PostgreSQL for the payments team.").Checked.Single().Flags.Should().NotContain(EditFlags.Length);
    }

    [Fact]
    public void The_locked_prefix_is_kept_exactly_and_a_repeated_prefix_is_dropped()
    {
        var (_, c) = Run("Languages:", "Languages: TypeScript, C#, SQL");
        c.Single().Proposed.Should().Be("Languages: TypeScript, C#, SQL");
        Run("Languages:", "TypeScript, C#, SQL").Checked.Single().Proposed.Should().Be("Languages: TypeScript, C#, SQL");
    }

    [Fact]
    public void An_edit_that_changes_nothing_is_dropped()
    {
        Run("Built REST", "Built  REST APIs for the payments team using C# and PostgreSQL.").Checked.Should().BeEmpty();
    }

    [Fact]
    public void Mixed_inline_formatting_is_flagged_formatting_simplified()
    {
        var m = ResumeExtractor.Extract(new MemoryStream(ResumeFixtures.Get("typed-bullets-bold-leadins").Docx));
        EditValidator.FormattingSimplified(m.Paragraphs.Single(p => p.Text.StartsWith("Data engineer", StringComparison.Ordinal)))
            .Should().BeTrue("a bold phrase sits inside the sentence");
        EditValidator.FormattingSimplified(m.Paragraphs.Single(p => p.Text.StartsWith("• Pipelines", StringComparison.Ordinal)))
            .Should().BeFalse("the bold label is the locked prefix, the rest is one style");

        var theme = ResumeExtractor.Extract(new MemoryStream(ResumeFixtures.Get("theme-fonts-spellcheck").Docx));
        EditValidator.FormattingSimplified(theme.Paragraphs.Single(p => p.Text.StartsWith("Full-stack", StringComparison.Ordinal)))
            .Should().BeFalse("runs split by spell-check marks look the same");
    }

    // ---------- numbers ----------

    [Theory]
    [InlineData("Grew revenue 30%", "30", "%")]
    [InlineData("handled 1,200 requests", "1200", "plain")]
    [InlineData("saved ₹ 2.50 crore", "2.5", "cur:inr|scale:crore")]
    [InlineData("a $3M budget", "3", "cur:usd|scale:m")]
    [InlineData("10x faster", "10", "x")]
    [InlineData("five engineers", "5", "plain")]
    [InlineData("twenty-one services", "21", "plain")]
    public void Numbers_are_read_with_their_kind(string text, string value, string kind)
    {
        FactGuard.Numbers(text).Should().ContainSingle().Which.Should().Match<NumberToken>(n => n.Value == value && n.Kind == kind);
    }

    [Theory]
    [InlineData("OAuth2 and EC2 on k8s")]
    [InlineData("built 3D views and 2FA")]
    [InlineData("ranked 1st in the hackathon")]
    [InlineData("one of the first teams")]
    public void Digits_inside_names_and_the_word_one_are_not_numbers(string text)
    {
        FactGuard.Numbers(text).Should().BeEmpty();
    }
}
