using Homepage.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Homepage.Server.Data;

/// <summary>For <c>dotnet ef migrations add</c>; no database connection is needed to create a migration.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        ServiceCollectionExtensions.ConfigureDb(options, "Host=localhost;Database=homepage_design;Username=homepage;Password=design");
        return new AppDbContext(options.Options);
    }
}
