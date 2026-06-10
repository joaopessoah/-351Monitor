using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace M351.Infrastructure.Data;

/// <summary>Fábrica de design-time para `dotnet ef migrations` (usa o banco dev local).</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<M351DbContext>
{
    public M351DbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<M351DbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=m351_dev;Username=postgres;Password=postgres")
            .Options;

        return new M351DbContext(options, new TenantContext());
    }
}
