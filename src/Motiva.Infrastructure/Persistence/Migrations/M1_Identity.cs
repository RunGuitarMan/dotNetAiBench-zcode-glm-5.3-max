using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Motiva.Infrastructure.Persistence.Migrations;

[DbContext(typeof(MotivaDbContext))]
[Migration("20261007000100_M1_Identity")]
public sealed class M1Identity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE companies (
                id uuid PRIMARY KEY,
                name text NOT NULL,
                time_zone_id text NOT NULL);
            CREATE TABLE employees (
                company_id uuid NOT NULL REFERENCES companies(id),
                master_id integer NOT NULL,
                is_active boolean NOT NULL DEFAULT true,
                version integer NOT NULL DEFAULT 1,
                created_at timestamptz NOT NULL,
                CONSTRAINT pk_employees PRIMARY KEY (company_id, master_id));
            CREATE TABLE employee_tags (
                company_id uuid NOT NULL,
                master_id integer NOT NULL,
                tag text NOT NULL,
                CONSTRAINT pk_employee_tags PRIMARY KEY (company_id, master_id, tag));
            CREATE TABLE wallets (
                company_id uuid NOT NULL,
                master_id integer NOT NULL,
                created_at timestamptz NOT NULL,
                CONSTRAINT pk_wallets PRIMARY KEY (company_id, master_id),
                CONSTRAINT fk_wallets_employee FOREIGN KEY (company_id, master_id)
                    REFERENCES employees (company_id, master_id));
            CREATE TABLE resources (
                company_id uuid NOT NULL REFERENCES companies(id),
                id uuid PRIMARY KEY,
                code_norm text NOT NULL,
                name text NOT NULL,
                status text NOT NULL DEFAULT 'Active',
                version integer NOT NULL DEFAULT 1,
                created_at timestamptz NOT NULL);
            CREATE UNIQUE INDEX ux_resources_code ON resources (company_id, code_norm);
            CREATE TABLE wallet_balances (
                company_id uuid NOT NULL,
                master_id integer NOT NULL,
                resource_id uuid NOT NULL REFERENCES resources (id),
                balance bigint NOT NULL DEFAULT 0,
                CONSTRAINT pk_wallet_balances PRIMARY KEY (company_id, master_id, resource_id),
                CONSTRAINT ck_wallet_balance_non_negative CHECK (balance >= 0));
            CREATE TABLE achievements (
                company_id uuid NOT NULL REFERENCES companies(id),
                id uuid PRIMARY KEY,
                code_norm text NOT NULL,
                name text NOT NULL,
                description text NULL,
                version integer NOT NULL DEFAULT 1);
            CREATE UNIQUE INDEX ux_achievements_code ON achievements (company_id, code_norm);
            CREATE TABLE purchase_systems (
                company_id uuid NOT NULL REFERENCES companies(id),
                id uuid PRIMARY KEY,
                code_norm text NOT NULL,
                name text NOT NULL,
                status text NOT NULL DEFAULT 'Active',
                version integer NOT NULL DEFAULT 1);
            CREATE UNIQUE INDEX ux_purchase_systems_code ON purchase_systems (company_id, code_norm);
            CREATE TABLE purchase_system_resources (
                purchase_system_id uuid NOT NULL REFERENCES purchase_systems (id) ON DELETE CASCADE,
                resource_id uuid NOT NULL REFERENCES resources (id),
                CONSTRAINT pk_purchase_system_resources PRIMARY KEY (purchase_system_id, resource_id));
            CREATE TABLE integration_grants (
                company_id uuid NOT NULL REFERENCES companies(id),
                id uuid PRIMARY KEY,
                subject text NOT NULL,
                kind text NOT NULL,
                campaign_id uuid NULL,
                resource_id uuid NULL,
                purchase_system_id uuid NULL,
                status text NOT NULL DEFAULT 'Active',
                version integer NOT NULL DEFAULT 1,
                created_at timestamptz NOT NULL,
                revoked_at timestamptz NULL);
            CREATE INDEX ix_integration_grants_lookup
                ON integration_grants (company_id, subject, kind, status);
            CREATE TABLE idempotency_keys (
                company_id uuid NOT NULL,
                initiator_key text NOT NULL,
                operation text NOT NULL,
                target_id text NOT NULL,
                key text NOT NULL,
                essential_data text NULL,
                response_status integer NULL,
                response_body text NULL,
                created_at timestamptz NOT NULL,
                CONSTRAINT pk_idempotency_keys PRIMARY KEY (company_id, initiator_key, operation, target_id, key));
            CREATE INDEX ix_idempotency_keys_created ON idempotency_keys (created_at);
            CREATE TABLE outbox_jobs (
                id uuid PRIMARY KEY,
                type text NOT NULL,
                payload jsonb NOT NULL,
                available_at timestamptz NOT NULL,
                attempts integer NOT NULL DEFAULT 0,
                status text NOT NULL DEFAULT 'Pending',
                locked_by text NULL,
                locked_until timestamptz NULL,
                created_at timestamptz NOT NULL,
                error text NULL);
            CREATE INDEX ix_outbox_jobs_due ON outbox_jobs (status, available_at);
            CREATE TABLE audit_records (
                company_id uuid NOT NULL,
                id uuid PRIMARY KEY,
                actor_type text NOT NULL,
                actor_master_id integer NULL,
                actor_subject text NULL,
                actor_is_admin boolean NOT NULL DEFAULT false,
                action text NOT NULL,
                entity_type text NOT NULL,
                entity_id text NOT NULL,
                changes_json jsonb NOT NULL DEFAULT '[]',
                created_at timestamptz NOT NULL);
            CREATE INDEX ix_audit_records_company_time ON audit_records (company_id, created_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS audit_records, outbox_jobs, idempotency_keys, integration_grants,
                purchase_system_resources, purchase_systems, achievements, wallet_balances,
                resources, wallets, employee_tags, employees, companies CASCADE;
            """);
    }
}
