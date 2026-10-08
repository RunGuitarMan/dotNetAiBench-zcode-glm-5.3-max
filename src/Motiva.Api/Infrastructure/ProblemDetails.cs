using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Motiva.Application.Common;

namespace Motiva.Api.Infrastructure;

/// <summary>Problem Details (RFC 9457) with a constant machine-readable code and traceId;
/// 401 from the authentication handler also becomes a proper body (stage-2 §4.2). No tokens,
/// keys or signed URLs ever leak into the payload. Latency/status observations go through the
/// application-level <see cref="IRequestMetrics"/> seam — no Infrastructure reference (L01).</summary>
public sealed class ProblemDetailsMiddleware(RequestDelegate next, IRequestMetrics metrics, ILogger<ProblemDetailsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await next(context);
            // Framework-produced empty error responses (model binding, auth) become Problem
            // Details with a constant code and traceId — the contract never returns bare 4xx (T04).
            metrics.ObserveRequest(
                context.Request.Method == "GET" || context.Request.Method == "HEAD" ? "read" : "write",
                stopwatch.Elapsed.TotalMilliseconds);
            if (context.Response.StatusCode >= 400)
            {
                metrics.Count("http:status:" + context.Response.StatusCode);
            }

            if (context.Response.StatusCode >= 400 && !context.Response.HasStarted && context.Response.ContentLength is null)
            {
                var (status, code, title) = context.Response.StatusCode switch
                {
                    400 => (400, "validation.failed", "The request payload is malformed."),
                    401 => (401, "auth.invalid-token", "The access token is missing or invalid."),
                    403 => (403, "authz.forbidden", "Insufficient permissions for the operation."),
                    404 => (404, "not-found", "Object not found."),
                    409 => (409, "conflict.state", "The operation conflicts with the current state."),
                    415 => (415, "validation.failed", "Unsupported content type."),
                    _ => (context.Response.StatusCode, "validation.failed", "The request was rejected."),
                };
                await WriteAsync(context, status, code, title);
            }
        }
        catch (MotivaException ex)
        {
            metrics.ObserveRequest(
                context.Request.Method == "GET" || context.Request.Method == "HEAD" ? "read" : "write",
                stopwatch.Elapsed.TotalMilliseconds);
            metrics.Count("http:status:" + MotivaException.HttpStatusOf(ex.Code));
            metrics.Count("errors:business:" + MotivaException.StringCodeOf(ex.Code));
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
        catch (BadHttpRequestException ex) when (ex.StatusCode == 400)
        {
            // Malformed JSON or query binding: contract 400 with code/traceId, not an empty body (T04).
            await WriteAsync(context, 400, "validation.failed", "The request payload is malformed.");
        }
        catch (System.Text.Json.JsonException)
        {
            await WriteAsync(context, 400, "validation.failed", "The request payload is not valid JSON.");
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
