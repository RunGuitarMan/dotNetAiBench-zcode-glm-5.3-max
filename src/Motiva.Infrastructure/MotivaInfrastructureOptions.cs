namespace Motiva.Infrastructure;

/// <summary>Infrastructure endpoints and tuning knobs; secrets come from the environment
/// (.env/Compose), never from Git (T10).</summary>
public sealed class MotivaInfrastructureOptions
{
    public const string SectionName = "Motiva";

    public string PostgresConnectionString { get; set; } = "Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva";

    public string ValkeyEndpoint { get; set; } = "localhost:6380";

    public string S3ServiceUrl { get; set; } = "http://localhost:9100";

    public string S3AccessKey { get; set; } = "motiva";

    public string S3SecretKey { get; set; } = "motiva-secret";

    public string S3Bucket { get; set; } = "motiva-exports";

    public string S3Region { get; set; } = "us-east-1";

    /// <summary>Public endpoint for presigned URLs (e.g. http://localhost:9100 when the API
    /// talks to S3 through the compose network). Empty = reuse S3ServiceUrl.</summary>
    public string S3PublicUrl { get; set; } = string.Empty;
}
