using Microsoft.AspNetCore.Mvc;
using Motiva.Api.Auth;
using Motiva.Application.Common;
using Motiva.Application.Dto;

namespace Motiva.Api.Endpoints;

/// <summary>Endpoint plumbing: actor resolution and canonical response writing (bodies produced
/// by the application layer are returned verbatim, so replays stay byte-identical).</summary>
internal static class EndpointHelpers
{
    public static ActorContext Actor(this HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            throw new MotivaException(ErrorCode.AuthInvalidToken);
        }

        return ActorContextResolver.Resolve(context.User);
    }

    public static IResult ToResult(this CommandResponse response)
    {
        if (response.Status == 204)
        {
            return Results.NoContent();
        }

        var headers = new Dictionary<string, string>();
        if (response.Location is not null)
        {
            headers["Location"] = response.Location;
        }

        if (response.ETag is not null)
        {
            headers["ETag"] = response.ETag;
        }

        return new RawJsonResult(response.Status, response.Body, headers);
    }

    public static IResult Ok<T>(T dto, string? etag = null)
    {
        var body = CanonicalJson.Serialize(dto);
        var headers = new Dictionary<string, string>();
        if (etag is not null)
        {
            headers["ETag"] = etag;
        }

        return new RawJsonResult(200, body, headers);
    }

    public static IResult Page<T>(Page<T> page, Func<T, object> map)
    {
        var body = CanonicalJson.Serialize(new PageDto(page.Items.Select(map).ToArray(), page.NextCursor));
        return new RawJsonResult(200, body, new Dictionary<string, string>());
    }

    private sealed record PageDto(IReadOnlyList<object> Items, string? NextCursor);
}

/// <summary>Writes a pre-serialized canonical body with explicit status and headers.</summary>
internal sealed class RawJsonResult(int status, string body, IReadOnlyDictionary<string, string> headers) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/json";
        foreach (var header in headers)
        {
            httpContext.Response.Headers[header.Key] = header.Value;
        }

        await httpContext.Response.WriteAsync(body);
    }
}
