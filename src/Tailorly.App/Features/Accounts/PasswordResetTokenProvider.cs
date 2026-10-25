using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Tailorly.App.Infrastructure.Data;

namespace Tailorly.App.Features.Accounts;

/// <summary>Reset links expire after 1 hour; email verification links keep the default provider's 24 hours (PRD F1).</summary>
public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public PasswordResetTokenProviderOptions()
    {
        Name = PasswordResetTokenProvider.ProviderName;
        TokenLifespan = TimeSpan.FromHours(1);
    }
}

public sealed class PasswordResetTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<PasswordResetTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<AppUser>> logger)
    : DataProtectorTokenProvider<AppUser>(dataProtectionProvider, options, logger)
{
    public const string ProviderName = "PasswordReset";
}
