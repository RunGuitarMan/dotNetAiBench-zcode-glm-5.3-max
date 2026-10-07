using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Motiva.Tests.Persistence;

/// <summary>Migrations on real PostgreSQL 17 (T01): from empty database and step-by-step
/// M1→M2→M3→M4; CHECK constraints and partial unique indexes behave as designed (§2.1).
/// Direct EF/SQL is allowed in this group only (T08).</summary>
public sealed class MigrationsTests : IAsyncLifetime
{
    private static readonly string BaseConnection = Environment.GetEnvironmentVariable("Motiva__PostgresConnectionString")
        ?? "Host=localhost;Port=5433;Database=motiva;Username=motiva;Password=motiva-stand";

    private string _databaseName = "motiva_persist_" + Guid.NewGuid().ToString("N")[..12];
    private string _connection = string.Empty;

    public async Task InitializeAsync()
    {
        _connection = ReplaceDatabase(BaseConnection, _databaseName);
        await using var admin = new NpgsqlConnection(BaseConnection);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand("CREATE DATABASE " + _databaseName, admin);
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var admin = new NpgsqlConnection(BaseConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '" + _databaseName + "'", admin);
        await drop.ExecuteNonQueryAsync();
        await using var dropDb = new NpgsqlCommand("DROP DATABASE IF EXISTS " + _databaseName, admin);
        await dropDb.ExecuteNonQueryAsync();
    }

    private static string ReplaceDatabase(string connection, string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection) { Database = database };
        return builder.ConnectionString;
    }

    private async Task ApplyMigrationsAsync(params string[] targetMigrations)
    {
        var options = new DbContextOptionsBuilder<Motiva.Infrastructure.Persistence.MotivaDbContext>()
            .UseNpgsql(_connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new Motiva.Infrastructure.Persistence.MotivaDbContext(options);
        var migrator = context.GetInfrastructure().GetRequiredService<IMigrator>();
        if (targetMigrations.Length == 0)
        {
            await migrator.MigrateAsync();
        }
        else
        {
            foreach (var target in targetMigrations)
            {
                await migrator.MigrateAsync(target);
            }
        }
    }

    private NpgsqlConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(_connection);
        connection.Open();
        return connection;
    }

    [Fact]
    public async Task Empty_database_gets_all_four_migration_groups()
    {
        await ApplyMigrationsAsync();
        await using var connection = OpenConnection();
        await using var check = new NpgsqlCommand(
            "SELECT count(*) FROM \"__EFMigrationsHistory\"", connection);
        var applied = (long)(await check.ExecuteScalarAsync() ?? 0);
        Assert.Equal(4, applied);
    }

    [Fact]
    public async Task Step_by_step_transitions_apply_cleanly_t01()
    {
        foreach (var target in new[]
                 {
                     "20261007000100_M1_Identity",
                     "20261007000200_M2_Campaigns",
                     "20261007000300_M3_Economy",
                     "20261007000400_M4_Exports",
                 })
        {
            await ApplyMigrationsAsync(target);
            // Each step leaves a consistent schema: the migration history accepts the next step.
        }

        await using var connection = OpenConnection();
        foreach (var table in new[]
                 {
                     "companies", "employees", "wallets", "resources", "campaigns", "budgets", "operations",
                     "progress_events", "export_requests", "download_links", "outbox_jobs", "audit_records",
                 })
        {
            await using var exists = new NpgsqlCommand(
                "SELECT to_regclass('public." + table + "') IS NOT NULL", connection);
            Assert.True((bool)(await exists.ExecuteScalarAsync() ?? false), "table " + table + " must exist");
        }
    }

    [Fact]
    public async Task Budget_check_rejects_negative_available_b19d()
    {
        await ApplyMigrationsAsync("20261007000200_M2_Campaigns");
        await using var connection = OpenConnection();
        var company = Guid.NewGuid();
        await using (var seed = new NpgsqlCommand(
                        "INSERT INTO companies (id, name, time_zone_id) VALUES (@c, 'n', 'Europe/Tallinn'); " +
                        "INSERT INTO campaigns (company_id, id, code_norm, name, season, owner_master_id, starts_at, ends_at, created_at) " +
                        "VALUES (@c, @camp, 'C', 'n', 2026, 1, now(), now() + interval '1 day', now()); " +
                        "INSERT INTO resources (company_id, id, code_norm, name, created_at) VALUES (@c, @r, 'A', 'n', now()); " +
                        "INSERT INTO budgets (campaign_id, resource_id, allocated_total, spent_total, returned_total) VALUES (@camp, @r, 10, 15, 0)",
                        connection))
        {
            seed.Parameters.AddWithValue("c", company);
            seed.Parameters.AddWithValue("camp", Guid.NewGuid());
            seed.Parameters.AddWithValue("r", Guid.NewGuid());
            var ex = await Assert.ThrowsAsync<PostgresException>(() => seed.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        }
    }

    [Fact]
    public async Task Wallet_balance_check_rejects_negative_b22d()
    {
        await ApplyMigrationsAsync();
        await using var connection = OpenConnection();
        var company = Guid.NewGuid();
        var resource = Guid.NewGuid();
        await using (var setup = new NpgsqlCommand(
                        "INSERT INTO companies (id, name, time_zone_id) VALUES (@c, 'n', 'Europe/Tallinn'); " +
                        "INSERT INTO employees (company_id, master_id, created_at) VALUES (@c, 7, now()); " +
                        "INSERT INTO wallets (company_id, master_id, created_at) VALUES (@c, 7, now()); " +
                        "INSERT INTO resources (company_id, id, code_norm, name, created_at) VALUES (@c, @r, 'A', 'n', now())",
                        connection))
        {
            setup.Parameters.AddWithValue("c", company);
            setup.Parameters.AddWithValue("r", resource);
            await setup.ExecuteNonQueryAsync();
        }

        await using (var seed = new NpgsqlCommand(
                        "INSERT INTO wallet_balances (company_id, master_id, resource_id, balance) VALUES (@c, 7, @r, -1)",
                        connection))
        {
            seed.Parameters.AddWithValue("c", company);
            seed.Parameters.AddWithValue("r", resource);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => seed.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        }
    }

    [Fact]
    public async Task Posted_reversal_is_unique_per_original_b25d()
    {
        await ApplyMigrationsAsync();
        await using var connection = OpenConnection();
        var company = Guid.NewGuid();
        var original = Guid.NewGuid();
        await using (var setup = new NpgsqlCommand(
                        "INSERT INTO companies (id, name, time_zone_id) VALUES (@c, 'n', 'Europe/Tallinn')", connection))
        {
            setup.Parameters.AddWithValue("c", company);
            await setup.ExecuteNonQueryAsync();
        }

        await using (var first = new NpgsqlCommand(
                         "INSERT INTO operations (company_id, id, kind, result, initiator_type, initiator_key, source_number, original_operation_id, created_at) " +
                         "VALUES (@c, @id, 'SpendReversal', 'Posted', 'user', 'user:1', 'C-1', @orig, now())", connection))
        {
            first.Parameters.AddWithValue("c", company);
            first.Parameters.AddWithValue("id", Guid.NewGuid());
            first.Parameters.AddWithValue("orig", original);
            await first.ExecuteNonQueryAsync();
        }

        await using (var second = new NpgsqlCommand(
                         "INSERT INTO operations (company_id, id, kind, result, initiator_type, initiator_key, source_number, original_operation_id, created_at) " +
                         "VALUES (@c, @id, 'SpendReversal', 'Posted', 'user', 'user:1', 'C-2', @orig, now())", connection))
        {
            second.Parameters.AddWithValue("c", company);
            second.Parameters.AddWithValue("id", Guid.NewGuid());
            second.Parameters.AddWithValue("orig", original);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        }

        // A Declined attempt never occupies the reversal slot (partial index on Posted only).
        await using (var declined = new NpgsqlCommand(
                         "INSERT INTO operations (company_id, id, kind, result, initiator_type, initiator_key, source_number, original_operation_id, created_at) " +
                         "VALUES (@c, @id, 'SpendReversal', 'Declined', 'user', 'user:1', 'C-3', @orig, now())", connection))
        {
            declined.Parameters.AddWithValue("c", company);
            declined.Parameters.AddWithValue("id", Guid.NewGuid());
            declined.Parameters.AddWithValue("orig", original);
            await declined.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Business_number_unique_per_company_initiator_kind_b24()
    {
        await ApplyMigrationsAsync();
        await using var connection = OpenConnection();
        var company = Guid.NewGuid();
        await using (var setup = new NpgsqlCommand(
                        "INSERT INTO companies (id, name, time_zone_id) VALUES (@c, 'n', 'Europe/Tallinn')", connection))
        {
            setup.Parameters.AddWithValue("c", company);
            await setup.ExecuteNonQueryAsync();
        }

        await using (var first = new NpgsqlCommand(
                         "INSERT INTO operations (company_id, id, kind, result, initiator_type, initiator_key, source_number, created_at) " +
                         "VALUES (@c, @id, 'Spend', 'Posted', 'user', 'user:1', 'S-1', now())", connection))
        {
            first.Parameters.AddWithValue("c", company);
            first.Parameters.AddWithValue("id", Guid.NewGuid());
            await first.ExecuteNonQueryAsync();
        }

        await using (var second = new NpgsqlCommand(
                         "INSERT INTO operations (company_id, id, kind, result, initiator_type, initiator_key, source_number, created_at) " +
                         "VALUES (@c, @id, 'Spend', 'Posted', 'user', 'user:1', 'S-1', now())", connection))
        {
            second.Parameters.AddWithValue("c", company);
            second.Parameters.AddWithValue("id", Guid.NewGuid());
            var ex = await Assert.ThrowsAsync<PostgresException>(() => second.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        }

        // Same number, different kind or initiator — allowed (campaign not part of the key).
        await using (var third = new NpgsqlCommand(
                         "INSERT INTO operations (company_id, id, kind, result, initiator_type, initiator_key, source_number, created_at) " +
                         "VALUES (@c, @id, 'ManualAward', 'Posted', 'user', 'user:1', 'S-1', now())", connection))
        {
            third.Parameters.AddWithValue("c", company);
            third.Parameters.AddWithValue("id", Guid.NewGuid());
            await third.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Campaign_code_unique_excludes_deleted_tombstones()
    {
        await ApplyMigrationsAsync("20261007000200_M2_Campaigns");
        await using var connection = OpenConnection();
        var company = Guid.NewGuid();
        await using (var setup = new NpgsqlCommand(
                        "INSERT INTO companies (id, name, time_zone_id) VALUES (@c, 'n', 'Europe/Tallinn')", connection))
        {
            setup.Parameters.AddWithValue("c", company);
            await setup.ExecuteNonQueryAsync();
        }

        var campaignId = Guid.NewGuid();
        await using (var first = new NpgsqlCommand(
                         "INSERT INTO campaigns (company_id, id, code_norm, name, season, owner_master_id, starts_at, ends_at, created_at) " +
                         "VALUES (@c, @id, 'REUSE', 'n', 2026, 1, now(), now() + interval '1 day', now())", connection))
        {
            first.Parameters.AddWithValue("c", company);
            first.Parameters.AddWithValue("id", campaignId);
            await first.ExecuteNonQueryAsync();
        }

        // Duplicate code in the same season is rejected.
        await using (var duplicate = new NpgsqlCommand(
                         "INSERT INTO campaigns (company_id, id, code_norm, name, season, owner_master_id, starts_at, ends_at, created_at) " +
                         "VALUES (@c, @id, 'REUSE', 'n', 2026, 1, now(), now() + interval '1 day', now())", connection))
        {
            duplicate.Parameters.AddWithValue("c", company);
            duplicate.Parameters.AddWithValue("id", Guid.NewGuid());
            await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync());
        }

        // A Deleted tombstone frees the code (§2.3).
        await using (var tomb = new NpgsqlCommand(
                         "UPDATE campaigns SET status = 'Deleted', code_norm = '#DEL-' || id::text WHERE id = @id", connection))
        {
            tomb.Parameters.AddWithValue("id", campaignId);
            await tomb.ExecuteNonQueryAsync();
        }

        await using (var reuse = new NpgsqlCommand(
                         "INSERT INTO campaigns (company_id, id, code_norm, name, season, owner_master_id, starts_at, ends_at, created_at) " +
                         "VALUES (@c, @id, 'REUSE', 'n2', 2026, 1, now(), now() + interval '1 day', now())", connection))
        {
            reuse.Parameters.AddWithValue("c", company);
            reuse.Parameters.AddWithValue("id", Guid.NewGuid());
            await reuse.ExecuteNonQueryAsync();
        }
    }
}
