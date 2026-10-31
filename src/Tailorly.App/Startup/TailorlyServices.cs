using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Anthropic;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Formatting.Compact;
using Tailorly.App.Ai;
using Tailorly.App.Api;
using Tailorly.App.Features.Accounts;
using Tailorly.App.Features.Digests;
using Tailorly.App.Features.Jobs;
using Tailorly.App.Features.Health;
using Tailorly.App.Features.Resumes;
using Tailorly.App.Features.Sources;
using Tailorly.App.Features.Sources.Adapters;
using Tailorly.App.Features.Tailoring;
using Tailorly.App.Infrastructure;
using Tailorly.App.Infrastructure.Ai;
using Tailorly.App.Infrastructure.Data;
using Tailorly.App.Infrastructure.Email;
using Tailorly.App.Infrastructure.Fx;
using Tailorly.App.Infrastructure.Jobs;
using Tailorly.App.Infrastructure.Logging;
using Tailorly.App.Infrastructure.Storage;
using Tailorly.Core;
using Tailorly.Core.Ports;

namespace Tailorly.App.Startup;

/// <summary>Service registration for the single Tailorly process (API + Hangfire).</summary>
public static class TailorlyServices
{
    public const long MaxJsonBodyBytes = 1 * 1024 * 1024;

    public static WebApplicationBuilder AddTailorly(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = MaxJsonBodyBytes; // POST /resumes raises it to 5 MB for that request
        });

        services.AddSerilog((sp, lc) => lc
            .ReadFrom.Configuration(config)
            .ReadFrom.Services(sp) // extra sinks registered in DI (the log-scan test captures events this way)
            .Enrich.FromLogContext()
            .Destructure.With<PiiDestructuringPolicy>()
            .Enrich.With<PiiScrubbingEnricher>()
            .WriteTo.Console(new CompactJsonFormatter()));

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Only Caddy can reach the app (internal Docker network), so trust its forwarded headers.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        });

        // Keys for auth cookies, antiforgery and signed email links. Persisted on a volume in Docker so restarts
        // and redeploys do not sign everyone out (SDD §16.3). Unset locally = default per-user key ring.
        var dp = services.AddDataProtection().SetApplicationName("Tailorly");
        if (config["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
            dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

        services.AddTailorlyProblemDetails();
        services.AddOpenApi("v1", o => o
            .AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info.Title = "Tailorly Remote API";
                doc.Info.Version = "v1";
                return Task.CompletedTask;
            })
            .AddDocumentTransformer<ProblemOpenApiTransformer>());
        services.AddHttpContextAccessor();

        // Data
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddDbContext<AppDbContext>(o => o
            .UseNpgsql(config.GetConnectionString("Default"))
            .UseSnakeCaseNamingConvention());
        services.AddScoped<Seeder>();
        services.AddScoped<MigrationRunner>();
        services.AddScoped<SystemQueries>();

        // Identity: Guid keys, cookie auth. API calls get 401/403 problem details, never redirects.
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.SignIn.RequireConfirmedEmail = false; // unverified users may sign in; protected actions check (M1)
                // Length-based policy (NIST 800-63B): 10+ characters, passphrases welcome (design: auth-B sign-up hint).
                o.Password.RequiredLength = 10;
                o.Password.RequiredUniqueChars = 4;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequireDigit = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireLowercase = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Tokens.PasswordResetTokenProvider = PasswordResetTokenProvider.ProviderName;
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddTokenProvider<PasswordResetTokenProvider>(PasswordResetTokenProvider.ProviderName);
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(24)); // verify link
        services.Configure<PasswordResetTokenProviderOptions>(_ => { });
        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = "__Host-tailorly";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.Path = "/";
            o.SlidingExpiration = true;
            o.ExpireTimeSpan = TimeSpan.FromDays(14);
            o.Events.OnRedirectToLogin = ctx => Problems.WriteAsync(ctx.HttpContext, StatusCodes.Status401Unauthorized,
                ErrorCodes.Unauthenticated, "Sign in required");
            o.Events.OnRedirectToAccessDenied = ctx => Problems.WriteAsync(ctx.HttpContext, StatusCodes.Status403Forbidden,
                ErrorCodes.Forbidden, "Forbidden");
        });
        services.AddTailorlyAuthorization();
        services.AddTailorlyAntiforgery();
        services.AddTailorlyRateLimiting(config);
        services.AddTailorlyHealthChecks(config);
        services.AddTailorlyHangfire(config);

        // Ports (the only interfaces; SDD §6.1)
        services.Configure<EmailOptions>(config.GetSection("Email"));
        services.Configure<StorageOptions>(config.GetSection("Storage"));
        services.AddSingleton<IFileStore, LocalFileStore>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        AddFx(services, config);
        AddAi(services, config);

        // Features (plain classes; no interfaces outside the five ports)
        services.Configure<AppOptions>(config.GetSection("App"));
        services.Configure<DeletionLogOptions>(config.GetSection("DeletionLog"));
        services.AddSingleton<AppLinks>();
        services.AddSingleton<AccountRateLimits>();
        services.AddSingleton<ConsentIpHasher>();
        services.AddSingleton<AccountDeletionTokens>();
        services.AddSingleton<DeletionLog>();
        services.AddScoped<AuditWriter>();
        services.AddScoped<AccountEmailJob>();
        services.AddScoped<PurgeDeletedAccountsJob>();
        services.AddScoped<ParseResumeJob>();
        services.AddScoped<SweepStuckWorkJob>();
        AddSources(services, config);
        AddDigests(services, config);
        AddTailoring(services, config);

        return builder;
    }

    /// <summary>ATS analysis, tailoring and cover letters (PRD F8, F9): scoring, the jobs, documents and PDF export.</summary>
    private static void AddTailoring(IServiceCollection services, IConfiguration config)
    {
        services.Configure<TailorOptions>(config.GetSection("Tailor"));
        services.Configure<CoverLetterOptions>(config.GetSection("CoverLetter"));
        services.Configure<PdfOptions>(config.GetSection("Pdf"));
        services.Configure<Tailorly.Core.Ats.AtsWeights>(config.GetSection("Ats:Weights"));
        services.AddSingleton<AtsScoring>();
        services.AddScoped<TailoringData>();
        services.AddScoped<TailoredDocuments>();
        services.AddScoped<AtsAnalysisJob>();
        services.AddScoped<TailorResumeJob>();
        services.AddScoped<FinalizeResumeJob>();
        services.AddScoped<CoverLetterJob>();
        // Gotenberg on the internal network (Compose profile "pdf"); used only when pdf_export.enabled is on.
        services.AddHttpClient<PdfExport>((sp, c) =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PdfOptions>>().Value;
            c.BaseAddress = new Uri(o.GotenbergUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds));
        });
    }

    /// <summary>Job sourcing (SDD §8): one HTTP client per source, the adapters (the IJobSource seam), runners and jobs.</summary>
    private static void AddSources(IServiceCollection services, IConfiguration config)
    {
        services.AddSourceHttpClients(config);
        services.AddSingleton<IJobSource, HimalayasSource>();
        services.AddSingleton<IJobSource, RemoteOkSource>();
        services.AddSingleton<IJobSource, GreenhouseSource>();
        services.AddSingleton<IJobSource, LeverSource>();
        services.AddSingleton<IJobSource, AshbySource>();
        services.AddSingleton<SourceRegistry>();
        services.AddScoped<CompanyLinker>();
        services.AddScoped<DedupeResolver>();
        services.AddScoped<JobIngestor>();
        services.AddScoped<FeedRunner>();
        services.AddScoped<BoardPoller>();
        services.AddScoped<FetchSourcesJob>();
        services.AddScoped<FetchSourceJob>();
        services.AddScoped<PollBoardJob>();
        services.AddScoped<RetentionJob>();
        services.AddScoped<BoardVerifier>();
        services.AddScoped<SeedLoader>();
        services.AddScoped<VerifyLinksJob>();
        services.AddScoped<Tailorly.App.Features.Admin.CoverageReport>();
    }

    /// <summary>Enrichment, scoring and the daily digest (PRD F5–F7): the per-user run, dispatcher, email and API.</summary>
    private static void AddDigests(IServiceCollection services, IConfiguration config)
    {
        services.Configure<EnrichmentOptions>(config.GetSection("Enrichment"));
        services.Configure<ScoringOptions>(config.GetSection("Scoring"));
        services.Configure<DigestOptions>(config.GetSection("Digest"));
        services.AddSingleton<UnsubscribeTokens>();
        services.AddSingleton<NotificationEmailTokens>();
        services.AddScoped<ScoredJobs>();
        services.AddScoped<EnrichmentService>();
        services.AddScoped<ScoringService>();
        services.AddScoped<PerUserRunJob>();
        services.AddScoped<DigestDispatcherJob>();
        services.AddScoped<DigestEmailJob>();
    }

    /// <summary>FX rates for salary comparison and "≈ INR" (<c>Fx:Provider</c>: Frankfurter, or Static for tests/offline).</summary>
    private static void AddFx(IServiceCollection services, IConfiguration config)
    {
        services.Configure<FxOptions>(config.GetSection("Fx"));
        services.AddSingleton<StaticFxRateProvider>();
        var provider = config["Fx:Provider"] ?? "Frankfurter";
        if (provider.Equals("Static", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IFxRateProvider>(sp => sp.GetRequiredService<StaticFxRateProvider>());
        }
        else if (provider.Equals("Frankfurter", StringComparison.OrdinalIgnoreCase))
        {
            FrankfurterFxRateProvider.AddClient(services);
            services.AddSingleton<IFxRateProvider, FrankfurterFxRateProvider>();
        }
        else
        {
            throw new InvalidOperationException($"Unknown Fx:Provider '{provider}'. Use Frankfurter or Static.");
        }
    }

    /// <summary>AI runner (SDD §9): routing from ai_task_config, prompt files, budgets, ledger, one provider.</summary>
    private static void AddAi(IServiceCollection services, IConfiguration config)
    {
        services.Configure<AiOptions>(config.GetSection("Ai"));
        services.Configure<BudgetOptions>(config.GetSection("Budgets"));
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PromptRegistry>();
        services.AddSingleton<TaskConfigCache>();
        services.AddSingleton<AiConcurrency>();
        services.AddScoped<BudgetService>();
        services.AddScoped<UsageLedger>();
        services.AddScoped<IAiTaskRunner, AiTaskRunner>();

        var provider = config["Ai:Provider"] ?? "Fake";
        if (provider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<FakeModelProvider>();
            services.AddSingleton<IModelProvider>(sp => sp.GetRequiredService<FakeModelProvider>());
        }
        else if (provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var o = config.GetSection("Anthropic").Get<AnthropicOptions>() ?? new AnthropicOptions();
            if (string.IsNullOrWhiteSpace(o.ApiKey))
                throw new InvalidOperationException("Ai:Provider is Anthropic but Anthropic:ApiKey (env Anthropic__ApiKey) is not set.");
            services.AddSingleton(new AnthropicClient
            {
                ApiKey = o.ApiKey,
                MaxRetries = o.MaxRetries,
                Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds),
            });
            services.AddSingleton<IModelProvider, AnthropicModelProvider>();
        }
        else
        {
            throw new InvalidOperationException($"Unknown Ai:Provider '{provider}'. Use Fake or Anthropic.");
        }
    }
}
