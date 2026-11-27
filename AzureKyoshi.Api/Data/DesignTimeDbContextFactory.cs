using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AzureKyoshi.Api.Data;

/// <summary>
/// Allows `dotnet ef migrations add` and `dotnet ef database update` to run without starting the Azure Functions host.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AzureKyoshiDbContext>
{
    public AzureKyoshiDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AzureKyoshiDbContext>();

        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Server=(localdb)\\mssqllocaldb;Database=AzureKyoshiDesignTime;Trusted_Connection=True;";

        optionsBuilder.UseSqlServer(connectionString, sql =>
            sql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(15), errorNumbersToAdd: null));
        return new AzureKyoshiDbContext(optionsBuilder.Options);
    }
}
