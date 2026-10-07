using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Motiva.Application.Common;
using Motiva.Application.Ports;
using StackExchange.Redis;

namespace Motiva.Infrastructure.Caching;

/// <summary>Valkey snapshot layer (§3.7): values carry {payload, asOfUtc, expiresAtUtc}; the age
/// is counted from asOfUtc so a late fill never extends a stale snapshot; expired or corrupt
/// values are a miss; a cache outage degrades to PostgreSQL after a bounded wait.</summary>
public sealed class ValkeyCacheSnapshots : ICacheSnapshots
{
    private readonly ConnectionMultiplexer _redis;
    private readonly ILogger<ValkeyCacheSnapshots> _logger;
    private readonly TimeSpan _boundedWait = TimeSpan.FromMilliseconds(200);

    public ValkeyCacheSnapshots(Microsoft.Extensions.Options.IOptions<MotivaInfrastructureOptions> optionsAccessor, ILogger<ValkeyCacheSnapshots> logger)
    {
        _redis = ConnectionMultiplexer.Connect(
            new ConfigurationOptions { EndPoints = { optionsAccessor.Value.ValkeyEndpoint }, AbortOnConnectFail = false, ConnectTimeout = 500 });
        _logger = logger;
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_boundedWait);
            var raw = await _redis.GetDatabase().StringGetAsync(new RedisKey(key)).WaitAsync(cts.Token);
            if (raw.IsNull)
            {
                return null;
            }

            var entry = CanonicalJson.Deserialize<CacheEntry>(raw.ToString());
            if (entry is null || entry.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                return null; // expired or corrupt: miss
            }

            return entry.Payload;
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException or OperationCanceledException or RedisConnectionException)
        {
            Log.CacheGetFailed(_logger, key, ex);
            return null;
        }
    }

    public async Task SetAsync(string key, string payload, DateTimeOffset asOfUtc, TimeSpan tolerance, CancellationToken ct)
    {
        var expiresAt = asOfUtc + tolerance;
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            return; // dead-born value: never written (A2-09)
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_boundedWait);
            var entry = CanonicalJson.Serialize(new CacheEntry(payload, asOfUtc, expiresAt));
            await _redis.GetDatabase().StringSetAsync(
                new RedisKey(key), new RedisValue(entry), expiry: expiresAt - DateTimeOffset.UtcNow).WaitAsync(cts.Token);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException or OperationCanceledException or RedisConnectionException)
        {
            Log.CacheSetFailed(_logger, key, ex);
        }
    }

    private sealed record CacheEntry(string Payload, DateTimeOffset AsOfUtc, DateTimeOffset ExpiresAtUtc);

    private static class Log
    {
        private static readonly Action<ILogger, string, Exception> GetFailed = LoggerMessage.Define<string>(
            LogLevel.Warning, new EventId(1, "ValkeyGetFailed"), "Valkey get failed for {Key}; degrading to PostgreSQL");

        private static readonly Action<ILogger, string, Exception> SetFailed = LoggerMessage.Define<string>(
            LogLevel.Warning, new EventId(2, "ValkeySetFailed"), "Valkey set failed for {Key}; snapshot not cached");

        public static void CacheGetFailed(ILogger logger, string key, Exception ex) => GetFailed(logger, key, ex);

        public static void CacheSetFailed(ILogger logger, string key, Exception ex) => SetFailed(logger, key, ex);
    }
}
