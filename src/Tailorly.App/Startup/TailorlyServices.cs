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
using Tailorly.App.Features.Health;
using Tailorly.App.Features.Resumes;
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
        services.AddSingleton<IFxRateProvider, StaticFxRateProvider>();
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
        // IJobSource adapters are registered in M2.

        return builder;
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
