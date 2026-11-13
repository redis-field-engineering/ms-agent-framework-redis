// Copyright (c) Redis, Inc.

using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using RedisIrisDevUI;

// Provisioning tools use these exports without starting the host or requiring credentials.
if (ContextExports.TryWrite(args)) return;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("RedisIrisDevUI is a local development sample and only runs in the Development environment.");
}

var settings = new SampleOptions(builder.Configuration);
builder.Logging.AddFilter("System.Net.Http.HttpClient.Redis.Iris.AgentMemory", LogLevel.Warning);
builder.AddSampleTelemetry();
builder.AddTravelAgent(settings);
builder.AddTravelAuthentication(settings);
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();
builder.AddDevUI(options =>
{
    // Compose binds the published port to host loopback; Docker forwarding is non-loopback inside the container.
    options.AllowRemoteAccess = settings.AllowRemoteAccess;
    if (settings.AuthenticationRequired)
    {
        options.ConfigureEndpoints = endpoints => endpoints.RequireAuthorization();
    }
});

using var telemetry = settings.ConsoleTelemetry ? SampleTelemetry.StartConsoleTelemetry() : null;
var app = builder.Build();
if (settings.AuthenticationRequired)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.MapTravelAuthentication(settings);
app.UseStaticFiles();
app.MapDevUI();

// Apply the same authorization policy to the agent APIs and every sample inspection/cleanup route.
var endpoints = app.MapGroup("");
if (settings.AuthenticationRequired) endpoints.RequireAuthorization();
endpoints.MapOpenAIResponses();
endpoints.MapOpenAIConversations();
endpoints.MapGet("/", (TravelUserAccessor users) => Results.Content(
    SamplePages.Dashboard(users.Current, settings.ShortTermWindow,
        settings.AuthenticationRequired, settings.AccessTokenInspectorEnabled), "text/html"));
endpoints.MapRamEndpoints(settings.ShortTermWindow);
endpoints.MapMemoryEditor(settings);
endpoints.MapContextEndpoints();

Console.WriteLine(settings.AuthenticationRequired
    ? $"Redis Iris DevUI authentication: {(settings.Oidc is not null ? "Cognito OIDC" : "development sign-in token")} mode"
    : $"Redis Iris DevUI owner: {settings.OwnerId} (legacy single-user mode)");
Console.WriteLine("Redis Context Retriever: configured (lazy MCP connection)");
Console.WriteLine("Dashboard: http://localhost:5080/");
Console.WriteLine("DevUI:     http://localhost:5080/devui");
app.Run();
