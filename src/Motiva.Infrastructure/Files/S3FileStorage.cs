using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Motiva.Application.Common;
using Motiva.Application.Ports;

namespace Motiva.Infrastructure.Files;

/// <summary>Private S3 (MinIO) object storage for export bytes (T07): per-attempt keys make
/// uploads put-if-absent by construction; download links are presigned for ≤ 60 s; the checksum
/// is computed over the stored object by the caller.</summary>
public sealed class S3FileStorage : IFileStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly ILogger<S3FileStorage> _logger;

    public S3FileStorage(Microsoft.Extensions.Options.IOptions<MotivaInfrastructureOptions> optionsAccessor, ILogger<S3FileStorage> logger)
    {
        var options = optionsAccessor.Value;
        _bucket = options.S3Bucket;
        _logger = logger;
        var config = new AmazonS3Config
        {
            ServiceURL = options.S3ServiceUrl,
            ForcePathStyle = true,
            AuthenticationRegion = options.S3Region,
            // An outage must surface as a bounded 424, not a hanging request (T06).
            Timeout = TimeSpan.FromSeconds(5),
        };
        _client = new AmazonS3Client(
            new BasicAWSCredentials(options.S3AccessKey, options.S3SecretKey), config);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, ct);
            return response is not null;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task PutAsync(string key, Stream content, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            ContentType = "text/csv",
        }, ct);
    }

    public async Task<DownloadLinkRec> CreateDownloadLinkAsync(string key, TimeSpan lifetime, CancellationToken ct)
    {
        var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : lifetime),
        });
        return new DownloadLinkRec(Guid.NewGuid(), url, DateTimeOffset.UtcNow.Add(lifetime > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : lifetime));
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        await EnsureBucketAsync(ct);
        string? continuation = null;
        while (true)
        {
            var list = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                ContinuationToken = continuation,
            }, ct);
            foreach (var summary in list.S3Objects)
            {
                try
                {
                    await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = summary.Key }, ct);
                }
                catch (AmazonS3Exception ex)
                {
                    Log.OnDeleteFailed(_logger, summary.Key, ex);
                }
            }

            if (!list.IsTruncated)
            {
                break;
            }

            continuation = list.NextContinuationToken;
        }

        await AbortIncompleteUploadsAsync(prefix, ct);
    }

    public async Task<string> ComputeChecksumAsync(string key, CancellationToken ct)
    {
        using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, ct);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(response.ResponseStream, ct);
        return Convert.ToHexString(hash);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, ct);
        return response.ResponseStream;
    }

    public async Task<long> GetSizeAsync(string key, CancellationToken ct)
    {
        var metadata = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, ct);
        return metadata.ContentLength;
    }

    private async Task AbortIncompleteUploadsAsync(string prefix, CancellationToken ct)
    {
        var uploads = await _client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = _bucket, Prefix = prefix }, ct);
        foreach (var upload in uploads.MultipartUploads)
        {
            await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = upload.Key,
                UploadId = upload.UploadId,
            }, ct);
        }
    }

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        try
        {
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket, UseClientRegion = false }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyOwnedByYou" || ex.ErrorCode == "BucketAlreadyExists")
        {
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private static class Log
    {
        private static readonly Action<ILogger, string, Exception> DeleteFailed = LoggerMessage.Define<string>(
            LogLevel.Warning, new EventId(1, "S3DeleteFailed"), "Failed to delete {Key} during cleanup");

        public static void OnDeleteFailed(ILogger logger, string key, Exception ex) => DeleteFailed(logger, key, ex);
    }
}
