using System.Text.RegularExpressions;

namespace Tailorly.Docx.Tailoring;

/// <summary>
/// Word-level text helpers for the truthfulness checks: tech spellings are folded (<c>C#</c> → <c>csharp</c>,
/// <c>.NET</c> → <c>dotnet</c>, <c>Node.js</c> → <c>nodejs</c>, <c>back-end</c> → <c>backend</c>), text is lower-cased and
/// split into words, and plurals are reduced. Mirrors <c>Tailorly.Core.Matching.TermMatcher</c>, which this project may
/// not reference.
/// </summary>
public static partial class TechText
{
    private static readonly (Regex Pattern, string Replacement)[] Folds =
    [
        (FoldCSharp(), " csharp "),
        (FoldFSharp(), " fsharp "),
        (FoldCpp(), " cpp "),
        (FoldDotnet(), " dotnet "),
        (FoldNode(), " nodejs "),
        (FoldBackend(), " backend "),
        (FoldFrontend(), " frontend "),
        (FoldFullstack(), " fullstack "),
    ];

    /// <summary>Lower-cased, folded, stemmed words.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var t = text.ToLowerInvariant();
        foreach (var (pattern, replacement) in Folds) t = pattern.Replace(t, replacement);
        return NonWord().Split(t).Where(w => w.Length > 0).Select(Stem).ToList();
    }

    /// <summary>The words of <paramref name="text"/> as one padded string (<c>" w1 w2 "</c>) for whole-phrase search.</summary>
    public static string Padded(string? text) => " " + string.Join(' ', Words(text)) + " ";

    /// <summary>True when every word of <paramref name="phrase"/> appears, in order and adjacent, in <paramref name="padded"/>.</summary>
    public static bool ContainsPhrase(string padded, string phrase)
    {
        var words = Words(phrase);
        return words.Count > 0 && padded.Contains(" " + string.Join(' ', words) + " ", StringComparison.Ordinal);
    }

    /// <summary>Reduces plurals (<c>apis</c> → <c>api</c>, <c>libraries</c> → <c>library</c>); short words are left alone.</summary>
    public static string Stem(string word)
    {
        if (word.Length <= 3) return word;
        if (AcronymPlurals.Contains(word)) return word[..^1]; // "apis" is a plural; "redis" and "analysis" are not
        if (word.EndsWith("ies", StringComparison.Ordinal)) return word[..^3] + "y";
        if (word.EndsWith("sses", StringComparison.Ordinal) || word.EndsWith("xes", StringComparison.Ordinal)
            || word.EndsWith("ches", StringComparison.Ordinal) || word.EndsWith("shes", StringComparison.Ordinal))
            return word[..^2];
        if (word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && !word.EndsWith("us", StringComparison.Ordinal)
            && !word.EndsWith("is", StringComparison.Ordinal))
            return word[..^1];
        return word;
    }

    /// <summary>Plurals of acronyms ending in "i" (stemmed to the acronym).</summary>
    private static readonly HashSet<string> AcronymPlurals = new(StringComparer.Ordinal) { "apis", "uis", "kpis", "clis", "guis", "rois" };

    /// <summary>
    /// Technologies that are facts even when written in lower case ("kubernetes", "kafka"). Capitalised and mixed-case
    /// terms are caught without a list; this covers the lower-case spellings a model might use. Stemmed forms (as
    /// <see cref="Words"/> returns them). Names that are also everyday English words (react, swift, spring, express, chef…)
    /// are left out: in lower case they are usually the English word, and capitalised they are caught anyway.
    /// </summary>
    public static readonly IReadOnlySet<string> Lexicon = new HashSet<string>(StringComparer.Ordinal)
    {
        // languages
        "python", "java", "javascript", "typescript", "golang", "kotlin", "scala", "php", "perl", "haskell", "elixir",
        "erlang", "clojure", "lua", "matlab", "cobol", "fortran", "groovy", "csharp", "fsharp", "cpp", "sql", "nosql", "html",
        "css", "sass", "bash", "powershell", "solidity",
        // frameworks and libraries
        "vue", "vuej", "svelte", "nextj", "nuxt", "django", "fastapi", "laravel", "nestj", "blazor", "aspnet", "dotnet",
        "nodej", "flutter", "xamarin", "tensorflow", "pytorch", "kera", "numpy", "scikit", "pyspark", "hadoop", "airflow",
        "dbt", "kafka", "rabbitmq", "rxj", "jquery", "tailwind", "graphql", "grpc", "webpack", "cypress", "selenium",
        "playwright", "junit", "pytest", "hibernate", "signalr", "langchain", "llamaindex",
        // data stores
        "postgre", "postgresql", "mysql", "mariadb", "mongodb", "mongo", "redis", "cassandra", "dynamodb", "elasticsearch",
        "opensearch", "bigquery", "redshift", "sqlite", "neo4j", "couchbase", "cockroachdb", "clickhouse", "databrick",
        "kinesis", "memcached", "sqlserver", "mssql",
        // cloud, infrastructure, tooling
        "aws", "azure", "gcp", "kubernete", "k8s", "docker", "terraform", "ansible", "jenkin", "circleci", "gitlab", "github",
        "bitbucket", "argocd", "istio", "prometheus", "grafana", "datadog", "splunk", "nginx", "linux", "serverless", "ec2",
        "eks", "aks", "gke", "openshift", "cloudformation", "pulumi", "sonarqube", "newrelic", "jira", "figma", "tableau",
        "powerbi", "salesforce", "servicenow", "microservice", "devop", "devsecop", "mlop", "sre", "etl", "elt", "oauth",
        "oauth2", "jwt", "saml", "sso", "ldap", "kerbero", "openai", "llm", "nlp", "kubeflow", "mlflow", "sagemaker",
        // compliance and standards
        "hipaa", "gdpr", "soc2", "pci", "hl7", "fhir",
    };

    /// <summary>
    /// Words that state credentials, seniority or scale; new ones are facts (degrees, certifications, awards, titles,
    /// "million users"). Stemmed forms.
    /// </summary>
    public static readonly IReadOnlySet<string> FactWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "bachelor", "master", "phd", "doctorate", "mba", "degree", "diploma", "certified", "certification", "certificate",
        "licensed", "license", "award", "awarded", "patent", "patented", "senior", "principal", "lead", "manager", "architect",
        "director", "vp", "cto", "ceo", "cfo", "founder", "cofounder", "head", "million", "billion", "thousand", "lakh", "crore",
    };

    /// <summary>Common abbreviations with internal dots that are not tech names.</summary>
    public static readonly IReadOnlySet<string> Abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "e.g", "i.e", "etc", "vs", "a.k.a", "approx", "incl", "esp", "and/or", "n/a", "w/",
    };

    [GeneratedRegex(@"(?<![a-z0-9])c\s?#|\bc-sharp\b", RegexOptions.None, 1000)]
    private static partial Regex FoldCSharp();

    [GeneratedRegex(@"(?<![a-z0-9])f\s?#", RegexOptions.None, 1000)]
    private static partial Regex FoldFSharp();

    [GeneratedRegex(@"(?<![a-z0-9])c\+\+", RegexOptions.None, 1000)]
    private static partial Regex FoldCpp();

    [GeneratedRegex(@"(?<![a-z0-9])\.net\b|\bdot\s?net\b|\basp\.net\b", RegexOptions.None, 1000)]
    private static partial Regex FoldDotnet();

    [GeneratedRegex(@"\bnode(\.js|js)?\b", RegexOptions.None, 1000)]
    private static partial Regex FoldNode();

    [GeneratedRegex(@"\bback[\s-]?end\b", RegexOptions.None, 1000)]
    private static partial Regex FoldBackend();

    [GeneratedRegex(@"\bfront[\s-]?end\b", RegexOptions.None, 1000)]
    private static partial Regex FoldFrontend();

    [GeneratedRegex(@"\bfull[\s-]?stack\b", RegexOptions.None, 1000)]
    private static partial Regex FoldFullstack();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.None, 1000)]
    private static partial Regex NonWord();
}
