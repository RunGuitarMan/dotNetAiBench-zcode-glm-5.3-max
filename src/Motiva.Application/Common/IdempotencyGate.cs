using Motiva.Application.Ports;

namespace Motiva.Application.Common;

public sealed record StoredEcho(int Status, string Body);

/// <summary>
/// Idempotency-Key gate for creating POSTs without a business number (T05). Scope:
/// company + verified initiator + operation/target + key. The same key with changed
/// essential data is a conflict; a successful replay returns the original response.
/// </summary>
public sealed class IdempotencyGate(IIdempotencyStore store)
{
    /// <summary>Returns the stored echo when the key is already committed, otherwise null.</summary>
    public async Task<StoredEcho?> BeginOrEchoAsync(
        Guid companyId,
        ActorContext actor,
        string operation,
        string targetId,
        string? key,
        string essentialData,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
        {
            throw new MotivaException(ErrorCode.ValidationFailed, "Idempotency-Key header is required (1..128).");
        }

        var result = await store.BeginAsync(companyId, actor.InitiatorKey, operation, targetId, key, essentialData, ct);
        if (!result.Exists)
        {
            return null;
        }

        if (!string.Equals(result.StoredData, essentialData, StringComparison.Ordinal))
        {
            throw new MotivaException(ErrorCode.ConflictIdempotencyData);
        }

        return new StoredEcho(result.StoredStatus ?? 201, result.StoredBody ?? "{}");
    }

    public Task CompleteAsync(
        Guid companyId,
        ActorContext actor,
        string operation,
        string targetId,
        string? key,
        int status,
        string body,
        CancellationToken ct)
    {
        return store.CompleteAsync(companyId, actor.InitiatorKey, operation, targetId, key!, status, body, ct);
    }
}
