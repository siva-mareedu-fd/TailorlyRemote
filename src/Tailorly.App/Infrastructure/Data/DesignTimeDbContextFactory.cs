using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Tailorly.App.Infrastructure.Data;

/// <summary>Used only by <c>dotnet ef</c> to create migrations; never connects at design time.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=tailorly_design")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options, new NoUser());
    }

    private sealed class NoUser : ICurrentUser
    {
        public Guid UserId => Guid.Empty;
    }
}
