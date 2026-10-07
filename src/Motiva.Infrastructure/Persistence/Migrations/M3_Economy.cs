using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Motiva.Infrastructure.Persistence.Migrations;

[DbContext(typeof(MotivaDbContext))]
[Migration("20261007000300_M3_Economy")]
public sealed class M3Economy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE operations (
                company_id uuid NOT NULL,
                id uuid PRIMARY KEY,
                kind text NOT NULL,
                result text NOT NULL,
                refusal_code text NULL,
                initiator_type text NOT NULL,
                initiator_key text NOT NULL,
                initiator_master_id integer NULL,
                initiator_subject text NULL,
                initiator_is_admin boolean NOT NULL DEFAULT false,
                master_id integer NULL,
                campaign_id uuid NULL,
                purchase_system_id uuid NULL,
                original_operation_id uuid NULL,
                reason text NULL,
                source_number text NOT NULL,
                created_at timestamptz NOT NULL,
                essential_data text NULL,
                response_status integer NULL,
                response_body text NULL);
            -- Business number scope (B24, §3.0): company + initiator + kind + number, no campaign.
            CREATE UNIQUE INDEX ux_operations_number
                ON operations (company_id, initiator_key, kind, source_number);
            -- At most one Posted reversal per original (B25.4).
            CREATE UNIQUE INDEX ux_operations_single_reversal
                ON operations (original_operation_id)
                WHERE kind IN ('AwardReversal', 'SpendReversal') AND result = 'Posted';
            CREATE INDEX ix_operations_employee_time ON operations (company_id, master_id, created_at);
            CREATE INDEX ix_operations_campaign_time ON operations (company_id, campaign_id, created_at);
            CREATE TABLE operation_items (
                operation_id uuid NOT NULL REFERENCES operations(id) ON DELETE CASCADE,
                resource_id uuid NOT NULL,
                resource_code text NOT NULL,
                amount bigint NOT NULL,
                is_debit boolean NOT NULL,
                CONSTRAINT pk_operation_items PRIMARY KEY (operation_id, resource_id));
            CREATE TABLE progress_events (
                company_id uuid NOT NULL,
                id uuid PRIMARY KEY,
                source_subject text NOT NULL,
                event_number text NOT NULL,
                result text NOT NULL,
                reject_reason text NULL,
                master_id integer NOT NULL,
                task_id uuid NOT NULL,
                period_start timestamptz NULL,
                transmitted_delta bigint NOT NULL,
                credited_delta bigint NOT NULL,
                accepted_at timestamptz NOT NULL,
                completion_id uuid NULL,
                stream_points_added bigint NULL,
                reward_outcome text NULL,
                reward_operation_id uuid NULL,
                completed_at timestamptz NULL,
                essential_data text NULL,
                response_status integer NULL,
                response_body text NULL);
            CREATE UNIQUE INDEX ux_progress_events_number
                ON progress_events (company_id, source_subject, event_number);
            CREATE INDEX ix_progress_events_employee_time
                ON progress_events (company_id, master_id, accepted_at);
            CREATE TABLE progress_state (
                company_id uuid NOT NULL,
                master_id integer NOT NULL,
                task_id uuid NOT NULL,
                period_start timestamptz NOT NULL,
                current bigint NOT NULL DEFAULT 0,
                CONSTRAINT pk_progress_state PRIMARY KEY (company_id, master_id, task_id, period_start),
                CONSTRAINT ck_progress_non_negative CHECK (current >= 0));
            CREATE TABLE completions (
                company_id uuid NOT NULL,
                master_id integer NOT NULL,
                task_id uuid NOT NULL,
                period_start timestamptz NOT NULL,
                id uuid PRIMARY KEY,
                stream_points bigint NOT NULL DEFAULT 0,
                award_operation_id uuid NULL,
                reward_outcome text NOT NULL DEFAULT 'NotProvided',
                completed_at timestamptz NOT NULL);
            CREATE UNIQUE INDEX ux_completions_once
                ON completions (company_id, master_id, task_id, period_start);
            CREATE TABLE stream_points (
                company_id uuid NOT NULL,
                master_id integer NOT NULL,
                stream_id uuid NOT NULL,
                season integer NOT NULL,
                points bigint NOT NULL DEFAULT 0,
                CONSTRAINT pk_stream_points PRIMARY KEY (company_id, master_id, stream_id, season),
                CONSTRAINT ck_stream_points_non_negative CHECK (points >= 0));
            CREATE TABLE achievement_grants (
                company_id uuid NOT NULL,
                master_id integer NOT NULL,
                achievement_id uuid NOT NULL,
                season integer NOT NULL,
                granted_at timestamptz NOT NULL,
                CONSTRAINT pk_achievement_grants PRIMARY KEY (company_id, master_id, achievement_id, season));
            CREATE TABLE challenge_scores (
                challenge_id uuid NOT NULL,
                master_id integer NOT NULL,
                score bigint NOT NULL DEFAULT 0,
                CONSTRAINT pk_challenge_scores PRIMARY KEY (challenge_id, master_id));
            CREATE TABLE challenge_results (
                challenge_id uuid NOT NULL,
                master_id integer NOT NULL,
                score bigint NOT NULL,
                place integer NOT NULL,
                finalized_at timestamptz NOT NULL,
                CONSTRAINT pk_challenge_results PRIMARY KEY (challenge_id, master_id));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS challenge_results, challenge_scores, achievement_grants,
                stream_points, completions, progress_state, progress_events, operation_items,
                operations CASCADE;
            """);
    }
}
