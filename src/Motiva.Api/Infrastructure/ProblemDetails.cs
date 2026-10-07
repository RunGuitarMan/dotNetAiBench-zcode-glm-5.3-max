using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Motiva.Application.Common;

namespace Motiva.Api.Infrastructure;

/// <summary>Problem Details (RFC 9457) with a constant machine-readable code and traceId;
/// 401 from the authentication handler also becomes a proper body (stage-2 §4.2). No tokens,
/// keys or signed URLs ever leak into the payload.</summary>
public sealed class ProblemDetailsMiddleware(RequestDelegate next, ILogger<ProblemDetailsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
            if (context.Response.StatusCode == 401 && !context.Response.HasStarted && context.Response.ContentLength is null)
            {
                await WriteAsync(context, 401, "auth.invalid-token", "The access token is missing or invalid.");
            }
            else if (context.Response.StatusCode == 403 && !context.Response.HasStarted && context.Response.ContentLength is null)
            {
                await WriteAsync(context, 403, "authz.forbidden", "Insufficient permissions for the operation.");
            }
        }
        catch (MotivaException ex)
        {
            var status = MotivaException.HttpStatusOf(ex.Code);
            if (status == 503)
            {
                context.Response.Headers.RetryAfter = "3";
            }

            await WriteAsync(context, status, MotivaException.StringCodeOf(ex.Code), ex.Detail ?? ex.Code.ToString(), ex.Errors);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsDatabaseUnavailable(ex))
        {
            // PostgreSQL unreachable: bounded 503 with Retry-After (T06), not a false success.
            context.Response.Headers.RetryAfter = "3";
            await WriteAsync(context, 503, "service.unavailable", "The service is temporarily unavailable.");
        }
        catch (Exception ex)
        {
            Log.OnUnhandled(logger, context.Request.Method, context.Request.Path, ex);
            await WriteAsync(context, 500, "internal.error", "An unexpected failure occurred.");
        }
    }

    /// <summary>Detects provider unavailability without referencing provider packages
    /// (Api must stay free of Npgsql/EF references — L01).</summary>
    internal static bool IsDatabaseUnavailable(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var name = current.GetType().FullName;
            if (name is not null && (name.StartsWith("Npgsql.", StringComparison.Ordinal) || name.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    internal static async Task WriteAsync(
        HttpContext context, int status, string code, string title, IReadOnlyDictionary<string, string>? errors = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        var payload = CanonicalJson.Serialize(new ProblemBody(
            "about:blank", title, status, code, traceId, errors));
        await context.Response.WriteAsync(payload);
    }

    private sealed record ProblemBody(
        string Type,
        string Title,
        int Status,
        string Code,
        string TraceId,
        IReadOnlyDictionary<string, string>? Errors);

    private static class Log
    {
        private static readonly Action<ILogger, string, string, Exception> Unhandled = LoggerMessage.Define<string, string>(
            LogLevel.Error, new EventId(1, "UnhandledFailure"), "Unhandled failure on {Method} {Path}");

        public static void OnUnhandled(ILogger logger, string method, string path, Exception ex)
            => Unhandled(logger, method, path, ex);
    }
}
