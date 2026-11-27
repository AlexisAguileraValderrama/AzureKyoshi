using AzureKyoshi.Api.Auth;
using AzureKyoshi.Api.Data;
using AzureKyoshi.Api.Functions;
using AzureKyoshi.Api.Options;
using AzureKyoshi.Api.Services;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.ConfigureFunctionsWebApplication();
builder.UseMiddleware<EntraJwtAuthenticationMiddleware>();

// Bind configuration options
builder.Services.Configure<AuthConfigurationOptions>(builder.Configuration.GetSection(AuthConfigurationOptions.SectionName));
builder.Services.Configure<AzureFoundryOptions>(builder.Configuration.GetSection(AzureFoundryOptions.SectionName));
builder.Services.Configure<MicrosoftLearnMcpOptions>(builder.Configuration.GetSection(MicrosoftLearnMcpOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));

builder.Services.AddHttpClient("MicrosoftLearnCatalog", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("AzureKyoshi/1.0");
});

// Database configuration: Azure SQL Server / LocalDB with Auto fallback to InMemory in Development
var dbOptions = builder.Configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var resolvedProvider = ResolveDatabaseProvider(dbOptions.Provider, connectionString, builder.Environment.IsDevelopment());

builder.Services.AddSingleton(new RuntimeDatabaseInfo(resolvedProvider.Label));
builder.Services.AddDbContext<AzureKyoshiDbContext>(options =>
{
    if (resolvedProvider.UseSqlServer && !string.IsNullOrWhiteSpace(connectionString))
    {
        options.UseSqlServer(connectionString, sql =>
            sql.EnableRetryOnFailure(maxRetryCount: 6, maxRetryDelay: TimeSpan.FromSeconds(15), errorNumbersToAdd: null));
    }
    else
    {
        options.UseInMemoryDatabase("AzureKyoshiDevInMemory");
    }
});

// Application services
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IMicrosoftLearnService, MicrosoftLearnMcpService>();
builder.Services.AddSingleton<IAiStudyService, AzureFoundryStudyService>();
builder.Services.AddScoped<IStudyWorkflowService, StudyWorkflowService>();

var app = builder.Build();

// Apply EF Core migrations automatically in Development (Production migrations run in CI/CD via efbundle)
if (builder.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AzureKyoshiDbContext>();
    if (db.Database.IsRelational())
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }
}

await app.RunAsync();

static (bool UseSqlServer, string Label) ResolveDatabaseProvider(string configuredProvider, string? connectionString, bool isDevelopment)
{
    if (configuredProvider.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
    {
        return (false, "In-Memory (Dev)");
    }

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return (false, "In-Memory (No connection string)");
    }

    if (configuredProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        return (true, isDevelopment ? "SQL Server" : "Azure SQL Server");
    }

    try
    {
        var csb = new SqlConnectionStringBuilder(connectionString)
        {
            ConnectTimeout = 2,
            InitialCatalog = "master"
        };
        using var conn = new SqlConnection(csb.ConnectionString);
        conn.Open();
        return (true, "SQL Server (LocalDB / SQL)");
    }
    catch
    {
        return (false, "In-Memory (SQL Server offline)");
    }
}
