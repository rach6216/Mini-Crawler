using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Crawler.Core.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> to generate migrations; no database connection is opened.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CrawlerDbContext>
{
    public CrawlerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CrawlerDbContext>()
            .UseNpgsql("Host=localhost;Database=crawler_design;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new CrawlerDbContext(options);
    }
}
