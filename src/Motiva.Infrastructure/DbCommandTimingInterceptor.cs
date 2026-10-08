using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Motiva.Infrastructure;

/// <summary>Measures REAL database command latency (T06): EF reports the actual ADO.NET
/// execution duration of every completed command; it is recorded per SQL verb — the published
/// DB series never substitutes HTTP measurements.</summary>
public sealed class DbCommandTimingInterceptor : DbCommandInterceptor
{
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Observe(command, eventData);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData);
        return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Observe(command, eventData);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override async ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData);
        return await base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Observe(command, eventData);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData);
        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    private static void Observe(System.Data.Common.DbCommand command, CommandExecutedEventData eventData)
    {
        var firstSpace = command.CommandText.IndexOf(' ');
        var verb = firstSpace > 0 ? command.CommandText[..firstSpace].ToUpperInvariant() : "OTHER";
        MotivaMetrics.ObserveDb(verb, eventData.Duration.TotalMilliseconds);
    }
}
