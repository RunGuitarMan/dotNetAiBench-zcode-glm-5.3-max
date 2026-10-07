using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Motiva.Infrastructure.Persistence.Migrations;

[DbContext(typeof(MotivaDbContext))]
[Migration("20261007000200_M2_Campaigns")]
public sealed class M2Campaigns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE campaigns (
                company_id uuid NOT NULL REFERENCES companies(id),
                id uuid PRIMARY KEY,
                code_norm text NOT NULL,
                name text NOT NULL,
                description text NULL,
                season integer NOT NULL,
                owner_master_id integer NOT NULL,
                starts_at timestamptz NOT NULL,
                ends_at timestamptz NOT NULL,
                status text NOT NULL DEFAULT 'Draft',
                version integer NOT NULL DEFAULT 1,
                created_at timestamptz NOT NULL,
                published_at timestamptz NULL);
            -- Codes are unique per (company, season) while the campaign is not a Deleted tombstone;
            -- the tombstone overwrites code_norm, freeing the code for reuse (§2.3).
            CREATE UNIQUE INDEX ux_campaigns_code ON campaigns (company_id, season, code_norm)
                WHERE status <> 'Deleted';
            CREATE TABLE campaign_tags (
                campaign_id uuid NOT NULL REFERENCES campaigns(id) ON DELETE CASCADE,
                kind text NOT NULL,
                tag text NOT NULL,
                CONSTRAINT pk_campaign_tags PRIMARY KEY (campaign_id, kind, tag));
            CREATE TABLE campaign_resources (
                campaign_id uuid NOT NULL REFERENCES campaigns(id) ON DELETE CASCADE,
                resource_id uuid NOT NULL REFERENCES resources(id),
                CONSTRAINT pk_campaign_resources PRIMARY KEY (campaign_id, resource_id));
            CREATE TABLE streams (
                campaign_id uuid NOT NULL REFERENCES campaigns(id) ON DELETE CASCADE,
                id uuid PRIMARY KEY,
                code_norm text NOT NULL,
                name text NOT NULL,
                status text NOT NULL DEFAULT 'Active',
                version integer NOT NULL DEFAULT 1);
            CREATE UNIQUE INDEX ux_streams_code ON streams (campaign_id, code_norm);
            CREATE TABLE tasks (
                stream_id uuid NOT NULL REFERENCES streams(id) ON DELETE CASCADE,
                id uuid PRIMARY KEY,
                code_norm text NOT NULL,
                name text NOT NULL,
                description text NULL,
                goal bigint NOT NULL,
                period text NOT NULL,
                stream_points bigint NOT NULL DEFAULT 0,
                status text NOT NULL DEFAULT 'Active',
                version integer NOT NULL DEFAULT 1);
            CREATE UNIQUE INDEX ux_tasks_code ON tasks (stream_id, code_norm);
            CREATE TABLE task_tags (
                task_id uuid NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                kind text NOT NULL,
                tag text NOT NULL,
                CONSTRAINT pk_task_tags PRIMARY KEY (task_id, kind, tag));
            CREATE TABLE task_reward_items (
                task_id uuid NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                resource_id uuid NOT NULL REFERENCES resources(id),
                amount bigint NOT NULL,
                CONSTRAINT pk_task_reward_items PRIMARY KEY (task_id, resource_id),
                CONSTRAINT ck_task_reward_amount_positive CHECK (amount > 0));
            CREATE TABLE milestones (
                stream_id uuid NOT NULL REFERENCES streams(id) ON DELETE CASCADE,
                id uuid PRIMARY KEY,
                threshold bigint NOT NULL,
                achievement_id uuid NOT NULL REFERENCES achievements(id),
                version integer NOT NULL DEFAULT 1);
            CREATE UNIQUE INDEX ux_milestones ON milestones (stream_id, threshold, achievement_id);
            CREATE TABLE challenges (
                campaign_id uuid NOT NULL REFERENCES campaigns(id) ON DELETE CASCADE,
                stream_id uuid NOT NULL REFERENCES streams(id) ON DELETE CASCADE,
                id uuid PRIMARY KEY,
                starts_at timestamptz NOT NULL,
                ends_at timestamptz NOT NULL,
                finalized_at timestamptz NULL,
                version integer NOT NULL DEFAULT 1);
            CREATE TABLE budgets (
                campaign_id uuid NOT NULL REFERENCES campaigns(id),
                resource_id uuid NOT NULL REFERENCES resources(id),
                allocated_total bigint NOT NULL DEFAULT 0,
                spent_total bigint NOT NULL DEFAULT 0,
                returned_total bigint NOT NULL DEFAULT 0,
                CONSTRAINT pk_budgets PRIMARY KEY (campaign_id, resource_id),
                CONSTRAINT ck_budget_available_non_negative
                    CHECK (allocated_total - spent_total + returned_total >= 0));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS budgets, challenges, milestones, task_reward_items, task_tags,
                tasks, streams, campaign_resources, campaign_tags, campaigns CASCADE;
            """);
    }
}
