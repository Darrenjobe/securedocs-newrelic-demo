using System.Diagnostics;
using System.Text;
using SecureDocs.Shared;
using SecureDocs.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages().AddMvcOptions(o => o.Filters.Add<PortalFaultPageFilter>());
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<PortalFaults>();

var apiBase = builder.Configuration["Api:BaseUrl"] ?? "http://localhost:5081";
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<UserForwardingHandler>();
builder.Services.AddHttpClient<SecureDocsApi>(c =>
{
    c.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(100);
}).AddHttpMessageHandler<UserForwardingHandler>();

// The traffic generator calls this portal's own URL.
var selfUrl = builder.Configuration["Portal:SelfUrl"] is { Length: > 0 } configuredSelf ? configuredSelf
    : (Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME") is { Length: > 0 } host ? $"https://{host}" : "http://localhost:5080");
builder.Services.AddHttpClient("self", c =>
{
    c.BaseAddress = new Uri(selfUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(100);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });
builder.Services.AddHostedService<TrafficGenerator>();

var app = builder.Build();

app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseRouting();

// Fake sign-in: pages need a selected client (cookie), everything else is open.
app.Use(async (ctx, next) =>
{
    var p = ctx.Request.Path;
    var open = p.StartsWithSegments("/Login") || p.StartsWithSegments("/fault") || p.StartsWithSegments("/demo")
               || p.StartsWithSegments("/Error") || p.StartsWithSegments("/health") || Path.HasExtension(p.Value);
    if (!open && string.IsNullOrEmpty(ctx.Request.Cookies[PortalSession.ClientCookie]))
    {
        ctx.Response.Redirect("/Login");
        return;
    }
    // Who is this request for? Lets Errors Inbox count users impacted, and slices APM data by client.
    Telemetry.Client(ctx.Request.Cookies[PortalSession.ClientCookie]);
    Telemetry.User(PortalSession.UserId(ctx));
    await next();
});

app.MapRazorPages();
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", api = apiBase }));

// ---------------- Fault Control panel endpoints ----------------
var fault = app.MapGroup("/fault");

fault.MapGet("/state", async (SecureDocsApi api, PortalFaults portal) =>
{
    var state = new FaultPanelState { Portal = portal.Snapshot() };
    try { state.Api = await api.GetFaultsAsync(); }
    catch (Exception ex) { state.ApiError = ex.Message; }
    return state;
});

fault.MapPost("/state", async (FaultPanelState body, SecureDocsApi api, PortalFaults portal) =>
{
    portal.Set(body.Portal);
    var state = new FaultPanelState { Portal = portal.Snapshot() };
    try { state.Api = await api.SetFaultsAsync(body.Api); }
    catch (Exception ex) { state.ApiError = ex.Message; }
    return state;
});

fault.MapPost("/reset", async (SecureDocsApi api, PortalFaults portal) =>
{
    portal.Set(new PortalFaultState { AutoTraffic = portal.Snapshot().AutoTraffic });
    await api.SetFaultsAsync(new ApiFaultState());
    return Results.Ok();
});

fault.MapPost("/trigger/{kind}", async (string kind, SecureDocsApi api, PortalFaults portal) =>
{
    var sw = Stopwatch.StartNew();
    Telemetry.Attr("fault.trigger", kind);
    switch (kind)
    {
        case "portal-exception":
            throw new PortalRenderException("Manual fault: portal failed to render claim summary for member session",
                new InvalidOperationException("Sequence contains no elements"));
        case "clear-backlog":
            return Results.Ok(new { kind, api = await api.TriggerAsync(kind) });
        case "traffic":
            portal.QueueTraffic(30);
            return Results.Ok(new { kind, message = "Queued 30 simulated portal requests" });
        default:
            try
            {
                var body = await api.TriggerAsync(kind);
                return Results.Ok(new { kind, elapsedMs = sw.ElapsedMilliseconds, api = body });
            }
            catch (ApiException ex)
            {
                // The portal saw the API fail: record it here too so both services show the error in the trace.
                Telemetry.NoticeError(ex);
                return Results.Json(new { kind, elapsedMs = sw.ElapsedMilliseconds, error = ex.UserMessage }, statusCode: 502);
            }
    }
});

// ---------------- Endpoints used by the traffic generator ----------------
var demo = app.MapGroup("/demo");
string ClientOf(HttpContext ctx)
{
    var code = ctx.Request.Cookies[PortalSession.ClientCookie] ?? "PCHP";
    Telemetry.Client(code);
    return code;
}

demo.MapPost("/submit-sample", async (HttpContext ctx, SecureDocsApi api, int? count) =>
{
    var docs = SyntheticData.GenerateDocuments(count ?? Random.Shared.Next(3, 16));
    var result = await api.SubmitBatchAsync(ClientOf(ctx), null, docs);
    return Results.Ok(result);
});

demo.MapPost("/upload-sample", async (HttpContext ctx, SecureDocsApi api) =>
{
    var csv = SyntheticData.ToCsv(SyntheticData.GenerateDocuments(Random.Shared.Next(5, 26)));
    await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csv));
    var t = await api.UploadFileAsync(ClientOf(ctx), $"{ClientOf(ctx)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv", ms);
    return Results.Ok(t);
});

demo.MapGet("/random-batch", async (HttpContext ctx, SecureDocsApi api) =>
{
    var batches = await api.GetBatchesAsync(ClientOf(ctx), 25);
    return batches.Count == 0 ? Results.Redirect("/Batches")
        : Results.Redirect($"/Batch?id={batches[Random.Shared.Next(batches.Count)].Id}");
});

app.Run();
