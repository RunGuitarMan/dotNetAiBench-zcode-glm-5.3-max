using Microsoft.AspNetCore.Authentication.JwtBearer;
using Motiva.Api.Auth;
using Motiva.Api.Endpoints;
using Motiva.Api.Infrastructure;
using Motiva.Application;
using Motiva.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMotivaApplication();
builder.Services.AddMotivaInfrastructure(options =>
    builder.Configuration.GetSection(MotivaInfrastructureOptions.SectionName).Bind(options));
builder.Services.AddMotivaJwtAuthentication();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseMiddleware<ProblemDetailsMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api/v1");
api.RequireAuthorization();

DirectoryEndpoints.Map(api);
CampaignEndpoints.Map(api);
EconomyEndpoints.Map(api);
ReadingEndpoints.Map(api);
ExportEndpoints.Map(api);

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (Motiva.Infrastructure.HealthChecks health) =>
{
    var ready = await health.DatabaseReachableAsync();
    return ready ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "degraded" }, statusCode: 503);
});
app.MapGet("/metrics", () => Results.Text(Motiva.Infrastructure.MotivaMetrics.RenderText(), "text/plain; version=0.0.4"));

app.Run();

/// <summary>Exposes the entry point for WebApplicationFactory-based HTTP tests.</summary>
public partial class Program;
