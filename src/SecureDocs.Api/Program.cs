using SecureDocs.Api.Data;
using SecureDocs.Api.Infrastructure;
using SecureDocs.Api.Services;
using SecureDocs.Api.Workers;
using SecureDocs.Shared;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Database: Azure SQL in the cloud, SQLite for quick local runs ----
var provider = builder.Configuration["Database:Provider"] ?? "SqlServer";
var connectionString = builder.Configuration.GetConnectionString("SecureDocs")
    ?? throw new InvalidOperationException("Connection string 'SecureDocs' is not configured");
builder.Services.AddDbContext<SecureDocsDb>(o =>
{
    if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
        o.UseSqlite(connectionString);
    }
    else o.UseSqlServer(connectionString, sql => sql.CommandTimeout(120));
});

// ---- The API calls its own address-verification endpoint over HTTP (shows as an external span) ----
var selfUrl = builder.Configuration["AddressService:BaseUrl"]
    ?? (Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME") is { Length: > 0 } host ? $"https://{host}" : "http://localhost:5081");
builder.Services.AddHttpClient<AddressVerificationClient>(c =>
{
    c.BaseAddress = new Uri(selfUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(60);
});

builder.Services.AddSingleton<FaultState>();
builder.Services.AddSingleton<FtpInbox>();
builder.Services.AddScoped<SqlFaultQueries>();
builder.Services.AddScoped<IngestionService>();
builder.Services.AddScoped<ProductionService>();
builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService<FtpIngestionWorker>();
builder.Services.AddHostedService<FtpPollingWorker>();
builder.Services.AddHostedService<ProductionFloorWorker>();
builder.Services.AddControllers();

SecureDocs.Shared.ClientProfiles.EnterpriseSlaMinutes = builder.Configuration.GetValue("Sla:EnterpriseMinutes", 15);
SecureDocs.Shared.ClientProfiles.StandardSlaMinutes = builder.Configuration.GetValue("Sla:StandardMinutes", 30);

var app = builder.Build();

// Unhandled exceptions → report to New Relic with full stack trace, return ProblemDetails JSON.
app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (ex is not null)
    {
        Telemetry.NoticeError(ex);
        app.Logger.LogError(ex, "Unhandled exception on {Path}", ctx.Request.Path);
    }
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new
    {
        type = ex?.GetType().Name ?? "Error",
        title = ex?.GetType().Name ?? "Error",
        detail = ex?.Message,
        status = 500,
        traceId = ctx.TraceIdentifier
    }, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
}));

// End user for Errors Inbox "users impacted": forwarded by the portal (X-SecureDocs-User).
app.Use(async (ctx, next) =>
{
    var user = ctx.Request.Headers[Telemetry.UserHeader].ToString();
    if (!string.IsNullOrEmpty(user)) Telemetry.User(user);
    await next();
});

app.MapGet("/", () => Results.Ok(new { service = "SecureDocs API", docs = "/api/clients", health = "/health" }));
app.MapControllers();

// Schema creation + demo data seeding run in the background (see DatabaseInitializer).
app.Run();
