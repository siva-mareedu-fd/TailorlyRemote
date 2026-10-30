using FluentAssertions;
using Tailorly.Core.Jobs;
using Tailorly.Core.Matching;

namespace Tailorly.Tests.Unit.Matching;

/// <summary>SDD §8.6: structured fields first, phrase rules next, then the model; hard rules win.</summary>
public sealed class EligibilityResolverTests
{
    [Theory]
    [InlineData(Eligibility.Unclear, Eligibility.Worldwide, Eligibility.Worldwide, EligibilitySource.Model)]
    [InlineData(Eligibility.Worldwide, Eligibility.IndiaEligible, Eligibility.IndiaEligible, EligibilitySource.Model)]
    [InlineData(Eligibility.Worldwide, Eligibility.Unclear, Eligibility.Unclear, EligibilitySource.Model)]
    [InlineData(Eligibility.IndiaEligible, Eligibility.Restricted, Eligibility.Restricted, EligibilitySource.Model)]
    public void The_model_decides_when_no_hard_rule_applies(string rule, string model, string expected, string source)
    {
        var hint = new EligibilityHint(rule, "phrase", rule == Eligibility.Unclear ? EligibilityBasis.None : EligibilityBasis.Phrase);
        var r = EligibilityResolver.Resolve(hint, model, "quoted evidence");
        r.Eligibility.Should().Be(expected);
        r.Source.Should().Be(source);
        r.Evidence.Should().Be("quoted evidence");
    }

    [Theory]
    [InlineData(Eligibility.Worldwide)]
    [InlineData(Eligibility.IndiaEligible)]
    [InlineData(Eligibility.Unclear)]
    public void An_explicit_restriction_beats_the_model(string model)
    {
        var hint = EligibilityRules.Classify(null, "Remote", "Backend Engineer", "Candidates must be authorized to work in the United States.");
        hint.Eligibility.Should().Be(Eligibility.Restricted);
        hint.IsHard.Should().BeTrue();
        var r = EligibilityResolver.Resolve(hint, model, "We hire globally");
        r.Eligibility.Should().Be(Eligibility.Restricted);
        r.Source.Should().Be(EligibilitySource.Rules);
        r.Evidence.Should().Contain("authorized to work in the United States");
    }

    [Theory]
    [InlineData(new string[0], Eligibility.Worldwide)]
    [InlineData(new[] { "India", "Canada" }, Eligibility.IndiaEligible)]
    [InlineData(new[] { "United States" }, Eligibility.Restricted)]
    public void A_structured_source_field_always_wins(string[] countries, string expected)
    {
        var hint = EligibilityRules.Classify(countries, null, "Engineer", "We hire anywhere in the world, US only for this role.");
        hint.Basis.Should().Be(EligibilityBasis.Structured);
        var r = EligibilityResolver.Resolve(hint, expected == Eligibility.Restricted ? Eligibility.Worldwide : Eligibility.Restricted, null);
        r.Eligibility.Should().Be(expected);
        r.Source.Should().Be(EligibilitySource.Source);
    }

    [Fact]
    public void Soft_rule_results_are_only_hints()
    {
        var hint = EligibilityRules.Classify(null, "Remote", "Engineer", "Work from anywhere.");
        hint.Eligibility.Should().Be(Eligibility.Worldwide);
        hint.IsHard.Should().BeFalse();
        EligibilityResolver.Resolve(hint, Eligibility.Unclear, null).Eligibility.Should().Be(Eligibility.Unclear);
    }

    [Fact]
    public void An_invalid_model_value_falls_back_to_the_rules()
    {
        var hint = EligibilityRules.Classify(null, "Remote - India", "Engineer", null);
        var r = EligibilityResolver.Resolve(hint, "maybe", null);
        r.Eligibility.Should().Be(Eligibility.IndiaEligible);
        r.Source.Should().Be(EligibilitySource.Rules);
    }

    [Theory]
    [InlineData(Eligibility.Worldwide, false, false, true)]
    [InlineData(Eligibility.IndiaEligible, true, false, true)]
    [InlineData(Eligibility.Unclear, false, false, true)]
    [InlineData(Eligibility.Unclear, true, false, false)]
    [InlineData(Eligibility.Unclear, true, true, true)]
    [InlineData(Eligibility.Restricted, false, true, false)]
    [InlineData(Eligibility.Restricted, true, true, false)]
    public void Display_rule(string eligibility, bool inEmail, bool showUnclear, bool visible) =>
        EligibilityResolver.Visible(eligibility, inEmail, showUnclear).Should().Be(visible);
}

public sealed class RawPrefilterTests
{
    private static readonly IReadOnlyList<string> Skills = ["C#", ".NET", "PostgreSQL", "REST APIs", "Docker"];

    private static RawCandidate Job(string title, string? location = "Remote", string? mode = WorkModes.Remote, string? type = null,
        string company = "Acme", string? head = null, IReadOnlyList<string>? countries = null) =>
        new(Guid.NewGuid(), title, company, location, mode, type, countries, head);

    private static MatchPreferences Prefs(string scope = Scope.IndiaPlusGlobalRemote, string[]? roles = null, string[]? modes = null) => new()
    {
        SearchScope = scope,
        TargetRoles = roles ?? ["Backend engineer", ".NET developer"],
        WorkModes = modes ?? [WorkModes.Remote],
        JobTypes = [EmploymentTypes.FullTime, EmploymentTypes.Contract],
        ExcludedCompanies = ["Fieldnote Labs"],
        ExcludedKeywords = ["unpaid", "commission only"],
    };

    [Theory]
    [InlineData("Senior Back-End Engineer")]
    [InlineData("Sr. .NET Developer")]
    [InlineData("Software Engineer, Backend (C#)")]
    [InlineData("Staff Engineer - Dotnet Platform")]
    public void Keeps_titles_that_match_a_target_role(string title) =>
        RawPrefilter.Apply(Job(title), Prefs(), Skills).Keep.Should().BeTrue();

    [Fact]
    public void Keeps_a_title_with_one_of_the_users_skills_or_a_description_with_two()
    {
        RawPrefilter.Apply(Job("Platform Engineer (PostgreSQL)"), Prefs(roles: []), Skills).Keep.Should().BeTrue();
        RawPrefilter.Apply(Job("Platform Engineer", head: "You will build REST APIs on PostgreSQL."), Prefs(roles: []), Skills)
            .Keep.Should().BeTrue();
        RawPrefilter.Apply(Job("Head of Marketing", head: "Grow our brand. Docker is not needed."), Prefs(), Skills)
            .Reason.Should().Be(RawPrefilter.NoMatch);
    }

    [Fact]
    public void Ranks_a_role_match_above_a_skill_mention()
    {
        var role = RawPrefilter.Apply(Job("Senior Backend Engineer"), Prefs(), Skills).Relevance;
        var skill = RawPrefilter.Apply(Job("Platform Engineer", head: "REST APIs on PostgreSQL"), Prefs(), Skills).Relevance;
        role.Should().BeGreaterThan(skill);
    }

    [Theory]
    [InlineData("Fieldnote Labs Inc.", "Backend Engineer", null, RawPrefilter.ExcludedCompany)]
    [InlineData("Acme", "Backend Engineer (unpaid internship)", null, RawPrefilter.ExcludedKeyword)]
    [InlineData("Acme", "Backend Engineer", "Pay is commission only.", RawPrefilter.ExcludedKeyword)]
    public void Drops_excluded_companies_and_keywords(string company, string title, string? head, string reason) =>
        RawPrefilter.Apply(Job(title, company: company, head: head), Prefs(), Skills).Reason.Should().Be(reason);

    [Fact]
    public void A_word_inside_another_word_is_not_an_excluded_keyword() =>
        RawPrefilter.Apply(Job("Backend Engineer", head: "Our unpaidinvoices product."), Prefs(), Skills).Keep.Should().BeTrue();

    [Fact]
    public void Drops_work_modes_and_job_types_the_user_did_not_choose()
    {
        RawPrefilter.Apply(Job("Backend Engineer", mode: WorkModes.Onsite, location: "Bengaluru"), Prefs(), Skills).Reason
            .Should().Be(RawPrefilter.WorkMode);
        RawPrefilter.Apply(Job("Backend Engineer", type: EmploymentTypes.Internship), Prefs(), Skills).Reason.Should().Be(RawPrefilter.JobType);
        RawPrefilter.Apply(Job("Backend Engineer", mode: null, type: null), Prefs(), Skills).Keep.Should().BeTrue("unknown values never drop a job");
    }

    [Fact]
    public void Drops_explicit_restrictions_before_any_model_call()
    {
        RawPrefilter.Apply(Job("Backend Engineer", location: "Remote - US only"), Prefs(), Skills).Reason.Should().Be(RawPrefilter.Restricted);
        RawPrefilter.Apply(Job("Backend Engineer", countries: ["United States", "Canada"]), Prefs(), Skills).Reason
            .Should().Be(RawPrefilter.Restricted);
    }

    [Theory]
    [InlineData(Scope.IndiaOnly, "Remote", WorkModes.Remote, false)]
    [InlineData(Scope.IndiaOnly, "Bengaluru, India", WorkModes.Hybrid, true)]
    [InlineData(Scope.IndiaOnly, "Remote - India", WorkModes.Remote, true)]
    [InlineData(Scope.GlobalRemoteOnly, "Bengaluru, India", WorkModes.Hybrid, false)]
    [InlineData(Scope.GlobalRemoteOnly, "Remote", WorkModes.Remote, true)]
    [InlineData(Scope.IndiaPlusGlobalRemote, "Remote", WorkModes.Remote, true)]
    [InlineData(Scope.IndiaPlusGlobalRemote, "Pune, India", WorkModes.Onsite, true)]
    [InlineData(Scope.IndiaPlusGlobalRemote, "Berlin", WorkModes.Onsite, false)]
    public void Applies_the_search_scope(string scope, string location, string mode, bool keep)
    {
        var prefs = Prefs(scope, modes: WorkModes.All);
        RawPrefilter.Apply(Job("Backend Engineer", location: location, mode: mode), prefs, Skills).Keep.Should().Be(keep);
    }

    [Fact]
    public void Keeps_everything_in_scope_when_the_user_has_no_roles_or_skills() =>
        RawPrefilter.Apply(Job("Underwater Basket Weaver"), Prefs(roles: []), []).Keep.Should().BeTrue();
}

public sealed class EnrichedPrefilterTests
{
    private static MatchPreferences Prefs(string scope = Scope.IndiaPlusGlobalRemote) => new()
    {
        SearchScope = scope,
        WorkModes = [WorkModes.Remote, WorkModes.Hybrid],
        JobTypes = [EmploymentTypes.FullTime],
        HiringModels = ["employee"],
        Locations = ["Bangalore", "Hyderabad"],
    };

    private static EnrichedFacts Facts(string eligibility = Eligibility.Worldwide, string? mode = WorkModes.Remote,
        string? type = EmploymentTypes.FullTime, string? hiring = "employee", string? location = "Remote", bool india = false) =>
        new(eligibility, mode, type, hiring, location, india);

    [Fact]
    public void Restricted_never_passes() =>
        EnrichedPrefilter.Check(Facts(Eligibility.Restricted), Prefs()).Should().Be(EnrichedPrefilter.Restricted);

    [Theory]
    [InlineData(Eligibility.Worldwide)]
    [InlineData(Eligibility.IndiaEligible)]
    [InlineData(Eligibility.Unclear)]
    public void Open_or_unclear_remote_jobs_pass(string eligibility) =>
        EnrichedPrefilter.Check(Facts(eligibility), Prefs()).Should().Be(EnrichedPrefilter.Kept);

    [Fact]
    public void India_only_needs_an_India_location()
    {
        EnrichedPrefilter.Check(Facts(), Prefs(Scope.IndiaOnly)).Should().Be(EnrichedPrefilter.OutOfScope);
        EnrichedPrefilter.Check(Facts(Eligibility.IndiaEligible, location: "Remote - India", india: true), Prefs(Scope.IndiaOnly))
            .Should().Be(EnrichedPrefilter.Kept);
    }

    [Fact]
    public void Checks_work_mode_job_type_and_hiring_model()
    {
        EnrichedPrefilter.Check(Facts(mode: WorkModes.Onsite, location: "Hyderabad", india: true), Prefs()).Should().Be(EnrichedPrefilter.WorkMode);
        EnrichedPrefilter.Check(Facts(type: EmploymentTypes.Contract), Prefs()).Should().Be(EnrichedPrefilter.JobType);
        EnrichedPrefilter.Check(Facts(hiring: "contractor"), Prefs()).Should().Be(EnrichedPrefilter.HiringModel);
        EnrichedPrefilter.Check(Facts(hiring: "unknown"), Prefs()).Should().Be(EnrichedPrefilter.Kept);
    }

    [Theory]
    [InlineData("Bengaluru, Karnataka", true)]
    [InlineData("Hyderabad", true)]
    [InlineData("Pune, India", false)]
    public void India_jobs_that_are_not_remote_must_be_in_one_of_the_users_cities(string location, bool keep) =>
        EnrichedPrefilter.Check(Facts(Eligibility.IndiaEligible, WorkModes.Hybrid, location: location, india: true), Prefs())
            .Should().Be(keep ? EnrichedPrefilter.Kept : EnrichedPrefilter.Location);
}

public sealed class RankingTests
{
    [Fact]
    public void Orders_by_fit_then_recency_with_rule_ranked_jobs_last()
    {
        var t = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var a = new RankedItem(Guid.NewGuid(), 8, false, t.AddDays(-2), t);
        var b = new RankedItem(Guid.NewGuid(), 9, false, t.AddDays(-3), t);
        var c = new RankedItem(Guid.NewGuid(), 8, false, t.AddHours(-1), t);
        var rules = new RankedItem(Guid.NewGuid(), 10, true, t, t);
        Ranking.Order([a, rules, c, b]).Select(x => x.JobId).Should().Equal(b.JobId, c.JobId, a.JobId, rules.JobId);
    }

    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(2, 4, 4, 8)]
    [InlineData(9, 10, 10, 10)]
    public void Rule_score_is_within_1_to_10(double relevance, int matched, int required, int expected) =>
        Ranking.RuleScore(relevance, matched, required).Should().Be(expected);
}

public sealed class TermMatcherTests
{
    [Theory]
    [InlineData("Senior C# Engineer", "csharp")]
    [InlineData("ASP.NET / .NET Core developer", "dotnet")]
    [InlineData("Node.js Back-end Developer", "nodejs")]
    [InlineData("Full Stack Engineer", "fullstack")]
    public void Folds_tech_spellings(string text, string word) => TermMatcher.Words(text).Should().Contain(word);

    [Fact]
    public void Short_skills_count_in_titles_only()
    {
        TermMatcher.SkillHits("Go Developer", ["Go"], allowShort: true).Should().Be(1);
        TermMatcher.SkillHits("You will go far here", ["Go"], allowShort: false).Should().Be(0);
    }

    [Fact]
    public void Generic_roles_match_engineering_titles() =>
        TermMatcher.RoleScore("Staff Developer, Payments", ["Software Engineer"]).Should().Be(1);
}
