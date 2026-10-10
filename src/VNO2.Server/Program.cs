using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using VNO2.Contracts;
using VNO2.Contracts.Community;
using VNO2.Server;
using VNO2.Server.Persistence;
using VNO2.Server.Security;
using VNO2.Server.Transport;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
var options = builder.Configuration.GetSection("Community").Get<CommunityOptions>() ?? new CommunityOptions();
if (!builder.Environment.IsDevelopment() && options.AllowGuestAuthentication)
    throw new InvalidOperationException("Guest authentication can only be enabled in Development");
if (options.LocalCapacity is < 1 or > 10000 || options.MaxConnections is < 1 or > 10000 || options.StartingCredits is < 0 or > 1_000_000_000 || options.ReconnectGraceSeconds is < 0 or > 86400)
    throw new InvalidOperationException("Community limits are outside their supported range");
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    forwarded.ForwardLimit = 1;
    forwarded.KnownProxies.Clear();
    forwarded.KnownIPNetworks.Clear();
    foreach (var proxy in options.TrustedProxies) forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<CommunityStore>();
builder.Services.AddSingleton<CommunityHub>();
builder.Services.AddSingleton<CommunitySocketEndpoint>();
builder.Services.AddHostedService<CommunityTimerService>();
builder.Services.AddHostedService<MasterHeartbeatService>();
builder.Services.AddHttpClient("MasterHeartbeat", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<JoinTicketVerifier>(client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("connections", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
if (options.TrustedProxies.Length > 0) app.UseForwardedHeaders();
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(25), KeepAliveTimeout = TimeSpan.FromSeconds(20) });
app.MapGet("/info", () => ServiceInfo.Create(ServiceRole.Server));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/community/info", () => new CommunityServerInfo(options.Id, options.Name, 1,
    app.Environment.IsDevelopment() && options.AllowGuestAuthentication, options.EconomyEnabled, options.FreeformResourcesEnabled));
app.MapGet("/api/community/rooms", async (CommunityStore store, CancellationToken cancellationToken) =>
    (await store.PublicInfoAsync(cancellationToken)).Rooms);
app.MapGet("/api/community/catalog", async (CommunityStore store, CancellationToken cancellationToken) =>
    (await store.PublicInfoAsync(cancellationToken)).Catalog);
app.MapGet("/api/community/media", async (CommunityStore store, CancellationToken cancellationToken) =>
    (await store.PublicInfoAsync(cancellationToken)).Media);
app.Map("/ws", async (HttpContext context, CommunitySocketEndpoint endpoint) => await endpoint.HandleAsync(context))
    .RequireRateLimiting("connections");
app.Run();

public partial class Program;
