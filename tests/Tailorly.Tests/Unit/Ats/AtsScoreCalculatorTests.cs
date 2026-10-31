using FluentAssertions;
using Tailorly.Core.Ats;

namespace Tailorly.Tests.Unit.Ats;

/// <summary>The ATS Match Score (PRD §8.2): formula, weights from configuration, and each deterministic component.</summary>
public sealed class AtsScoreCalculatorTests
{
    private static readonly AtsWeights Prd = new();

    private static SkillTerm S(string skill, params string[] aliases) => new(skill, aliases);

    // ---------- the formula ----------

    [Fact]
    public void Default_weights_are_the_PRD_weights()
    {
        Prd.KeywordCoverage.Should().Be(0.35);
        Prd.RequirementsMatch.Should().Be(0.25);
        Prd.TitleSeniority.Should().Be(0.10);
        Prd.ExperienceRelevance.Should().Be(0.15);
        Prd.FormatHealth.Should().Be(0.15);
    }

    [Fact]
    public void Score_is_the_weighted_sum_times_100()
    {
        // 0.35·0.6 + 0.25·0.5 + 0.10·1 + 0.15·0.8 + 0.15·0.9 = 0.21 + 0.125 + 0.1 + 0.12 + 0.135 = 0.69
        AtsScoreCalculator.Combine(new AtsComponents(0.6, 0.5, 1.0, 0.8, 0.9), Prd).Score.Should().Be(69);
        AtsScoreCalculator.Combine(new AtsComponents(1, 1, 1, 1, 1), Prd).Score.Should().Be(100);
        AtsScoreCalculator.Combine(new AtsComponents(0, 0, 0, 0, 0), Prd).Score.Should().Be(0);
    }

    [Fact]
    public void Rounds_half_away_from_zero()
    {
        // Only format health present: 0.585 → 58.5 → 59.
        AtsScoreCalculator.Combine(new AtsComponents(null, null, null, null, 0.585), Prd).Score.Should().Be(59);
    }

    [Fact]
    public void Missing_components_are_left_out_and_the_rest_rescaled()
    {
        // No keyword data: (0.25·1 + 0.10·0 + 0.15·0 + 0.15·0) / 0.65 = 0.3846 → 38
        AtsScoreCalculator.Combine(new AtsComponents(null, 1, 0, 0, 0), Prd).Score.Should().Be(38);
        AtsScoreCalculator.Combine(new AtsComponents(null, null, null, null, null), Prd).Score.Should().Be(0);
    }

    [Fact]
    public void Weights_come_from_configuration()
    {
        var keywordsOnly = new AtsWeights { KeywordCoverage = 1, RequirementsMatch = 0, TitleSeniority = 0, ExperienceRelevance = 0, FormatHealth = 0 };
        AtsScoreCalculator.Combine(new AtsComponents(0.42, 1, 1, 1, 1), keywordsOnly).Score.Should().Be(42);
    }

    [Fact]
    public void Components_are_clamped_to_0_1_and_rounded_to_3_places()
    {
        var r = AtsScoreCalculator.Combine(new AtsComponents(1.7, -0.2, 0.123456, double.NaN, 0.5), Prd);
        r.Components.Should().Be(new AtsComponents(1, 0, 0.123, null, 0.5));
    }

    // ---------- requirements_match ----------

    [Fact]
    public void Requirements_score_met_1_partial_half_missing_0_with_preferred_at_half_weight()
    {
        AtsScoreCalculator.RequirementsMatch([new("required", "met"), new("required", "partial"), new("required", "missing")])
            .Should().BeApproximately(0.5, 1e-9);
        // required met (1·1) + preferred missing (0.5·0) = 1 / 1.5
        AtsScoreCalculator.RequirementsMatch([new("required", "met"), new("preferred", "missing")]).Should().BeApproximately(2 / 3.0, 1e-9);
        AtsScoreCalculator.RequirementsMatch([]).Should().BeNull();
    }

    [Fact]
    public void Experience_relevance_is_the_clamped_label()
    {
        AtsScoreCalculator.ExperienceRelevance(0.7).Should().Be(0.7);
        AtsScoreCalculator.ExperienceRelevance(1.4).Should().Be(1);
        AtsScoreCalculator.ExperienceRelevance(null).Should().BeNull();
    }

    // ---------- keyword_coverage ----------

    private static readonly string[] Resume =
    [
        "Asha Verma",
        "Backend engineer building .NET services and REST APIs for payments teams.",
        "Built microservices in C# with PostgreSQL and Docker.",
        "Languages: C#, SQL, Go",
    ];

    [Fact]
    public void Required_skills_weigh_1_and_preferred_half()
    {
        var job = new JobSkillSet([S("C#"), S("Kafka")], [S("Docker"), S("Terraform")]);
        var r = KeywordCoverage.Compute(job, Resume);
        // found: C# (1) + Docker (0.5) of 1 + 1 + 0.5 + 0.5 = 1.5 / 3
        r.Score.Should().BeApproximately(0.5, 1e-9);
        r.Matched.Should().Equal("C#", "Docker");
        r.Missing.Should().Equal("Kafka", "Terraform");
        r.MissingRequired.Should().Equal("Kafka");
    }

    [Fact]
    public void Aliases_folded_spellings_case_and_plurals_match()
    {
        var job = new JobSkillSet(
            [S("PostgreSQL", "Postgres"), S("dotnet"), S("REST API"), S("Microservice"), S("c#")],
            [S("Postgres", "PostgreSQL")]);
        var r = KeywordCoverage.Compute(job, Resume);
        r.Missing.Should().BeEmpty();
        r.Score.Should().Be(1);
    }

    [Fact]
    public void A_skill_listed_as_required_and_preferred_counts_once_as_required()
    {
        var r = KeywordCoverage.Compute(new JobSkillSet([S("Kafka")], [S("kafka")]), Resume);
        r.Score.Should().Be(0);
        r.Missing.Should().Equal("Kafka");
    }

    [Fact]
    public void Short_skills_match_only_with_the_same_capitalisation()
    {
        KeywordCoverage.Compute(new JobSkillSet([S("Go")], []), Resume).Score.Should().Be(1);
        KeywordCoverage.Compute(new JobSkillSet([S("Go")], []), ["We go live every Friday."]).Score.Should().Be(0);
        KeywordCoverage.Compute(new JobSkillSet([S("C")], []), ["Languages: C#"]).Score.Should().Be(0, "C is not C#");
    }

    [Fact]
    public void A_phrase_never_matches_across_two_lines()
    {
        KeywordCoverage.Compute(new JobSkillSet([S("Machine Learning Ops")], []), ["machine learning", "ops team"]).Score.Should().Be(0);
    }

    [Fact]
    public void No_job_skills_means_no_keyword_component()
    {
        KeywordCoverage.Compute(new JobSkillSet([], []), Resume).Score.Should().BeNull();
    }

    // ---------- title_seniority ----------

    [Fact]
    public void Same_level_and_enough_years_score_1()
    {
        TitleSeniority.Compute(new SeniorityInput("senior", 5, 8, ["Senior Software Engineer"], 6, null)).Should().Be(1);
    }

    [Fact]
    public void Level_distance_and_missing_years_lower_the_rules_part()
    {
        // junior title (1) vs senior job (3): distance 2 → 0.4; 3 of 5 years → 0.6; mean 0.5
        TitleSeniority.Compute(new SeniorityInput("senior", 5, null, ["Junior Developer"], 3, null)).Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void The_model_label_is_averaged_with_the_rules()
    {
        TitleSeniority.Compute(new SeniorityInput("senior", 5, null, ["Senior Engineer"], 6, 0.6)).Should().BeApproximately(0.8, 1e-9);
        TitleSeniority.Compute(new SeniorityInput(null, null, null, [], null, 0.3)).Should().Be(0.3);
        TitleSeniority.Compute(new SeniorityInput(null, null, null, [], null, null)).Should().BeNull();
    }

    [Fact]
    public void Without_a_level_word_the_level_comes_from_years()
    {
        TitleSeniority.CandidateLevel(["Software Engineer"], 6).Should().Be(3);
        TitleSeniority.CandidateLevel(["Software Engineer"], 1).Should().Be(1);
        TitleSeniority.CandidateLevel(["Staff Engineer"], 1).Should().Be(4);
        TitleSeniority.CandidateLevel([], null).Should().Be(2);
    }

    [Fact]
    public void Far_over_the_maximum_years_scores_0_8()
    {
        TitleSeniority.YearsScore(new SeniorityInput(null, 2, 4, [], 12, null)).Should().Be(0.8);
        TitleSeniority.YearsScore(new SeniorityInput(null, 2, 4, [], 6, null)).Should().Be(1);
    }

    // ---------- format_health ----------

    private static FormatInputs Clean() => new(false, false, false, false, false, false, ["Calibri"], 600, 2, false, true, true);

    [Fact]
    public void A_clean_single_column_resume_is_fully_healthy()
    {
        var r = FormatHealth.Evaluate(Clean());
        r.Score.Should().Be(1);
        r.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Each_problem_costs_its_penalty_and_explains_itself()
    {
        var r = FormatHealth.Evaluate(Clean() with { HasColumns = true, HasTables = true, HeaderFooterText = true, HeaderFooterContact = true });
        r.Score.Should().BeApproximately(1 - 0.15 - 0.10 - 0.15, 1e-9);
        r.Findings.Select(f => f.Code).Should().BeEquivalentTo(["columns", "tables", "header_footer_contact"],
            "header text that is contact details is reported once, as contact");
        r.Findings.Should().OnlyContain(f => f.Suggestion != null && f.Message.Length > 0);
    }

    [Fact]
    public void Unusual_fonts_are_named_and_symbol_fonts_ignored()
    {
        var r = FormatHealth.Evaluate(Clean() with { Fonts = ["Calibri", "Wingdings", "Papyrus"] });
        r.Findings.Should().ContainSingle(f => f.Code == "unusual_fonts").Which.Message.Should().Contain("Papyrus").And.NotContain("Wingdings");
    }

    [Fact]
    public void Every_problem_at_once_still_leaves_a_score_between_0_and_1()
    {
        var bad = new FormatInputs(true, true, true, true, true, true, ["Papyrus"], 3000, 6, true, false, false);
        var r = FormatHealth.Evaluate(bad);
        r.Score.Should().BeApproximately(0.1, 1e-9, "the penalties add up to 0.9 at most");
        r.Findings.Select(f => f.Code).Should().Contain(["nonstandard_headings", "too_long", "tracked_changes", "text_boxes", "images"]);
        r.Findings.Should().NotContain(f => f.Code == "missing_contact", "contact details in the header are still contact details");
    }
}
