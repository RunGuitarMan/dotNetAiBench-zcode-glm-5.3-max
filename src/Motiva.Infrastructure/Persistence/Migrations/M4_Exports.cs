using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Motiva.Infrastructure.Persistence.Migrations;

[DbContext(typeof(MotivaDbContext))]
[Migration("20261007000400_M4_Exports")]
public sealed class M4Exports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE export_requests (
                company_id uuid NOT NULL,
                id uuid PRIMARY KEY,
                requested_by_master_id integer NOT NULL,
                scope text NOT NULL,
                target_master_id integer NULL,
                resource_id uuid NULL,
                from_utc timestamptz NOT NULL,
                to_utc timestamptz NOT NULL,
                status text NOT NULL DEFAULT 'Pending',
                generation integer NOT NULL DEFAULT 0,
                frozen_at timestamptz NULL,
                snapshot_saved boolean NOT NULL DEFAULT false,
                ready_at timestamptz NULL,
                s3_key text NULL,
                s3_version text NULL,
                size_bytes bigint NULL,
                checksum text NULL,
                error_detail text NULL,
                created_at timestamptz NOT NULL,
                lease_owner text NULL,
                lease_until timestamptz NULL,
                cleanup_intent boolean NOT NULL DEFAULT false);
            CREATE INDEX ix_export_requests_company ON export_requests (company_id, created_at);
            CREATE TABLE export_operation_ids (
                export_id uuid NOT NULL REFERENCES export_requests(id) ON DELETE CASCADE,
                operation_id uuid NOT NULL,
                CONSTRAINT pk_export_operation_ids PRIMARY KEY (export_id, operation_id));
            CREATE TABLE download_links (
                id uuid PRIMARY KEY,
                export_id uuid NOT NULL REFERENCES export_requests(id) ON DELETE CASCADE,
                idem_key text NOT NULL,
                expires_at timestamptz NOT NULL,
                created_at timestamptz NOT NULL);
            CREATE INDEX ix_download_links_expiry ON download_links (expires_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS download_links, export_operation_ids, export_requests CASCADE;");
    }
}
