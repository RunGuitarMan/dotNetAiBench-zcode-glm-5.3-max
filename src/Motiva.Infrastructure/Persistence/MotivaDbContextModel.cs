using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Motiva.Infrastructure.Persistence;

/// <summary>EF mapping of the storage model (§2.1 stage-2). CHECK constraints and partial
/// unique indexes are installed by the hand-written migrations M1–M4; the mapping mirrors
/// them so that runtime reads/writes stay in sync with the schema.</summary>
public sealed class MotivaDbContext(DbContextOptions<MotivaDbContext> options) : DbContext(options)
{
    public DbSet<CompanyRow> Companies => Set<CompanyRow>();
    public DbSet<EmployeeRow> Employees => Set<EmployeeRow>();
    public DbSet<EmployeeTagRow> EmployeeTags => Set<EmployeeTagRow>();
    public DbSet<WalletRow> Wallets => Set<WalletRow>();
    public DbSet<WalletBalanceRow> WalletBalances => Set<WalletBalanceRow>();
    public DbSet<ResourceRow> Resources => Set<ResourceRow>();
    public DbSet<AchievementRow> Achievements => Set<AchievementRow>();
    public DbSet<PurchaseSystemRow> PurchaseSystems => Set<PurchaseSystemRow>();
    public DbSet<PurchaseSystemResourceRow> PurchaseSystemResources => Set<PurchaseSystemResourceRow>();
    public DbSet<IntegrationGrantRow> IntegrationGrants => Set<IntegrationGrantRow>();
    public DbSet<CampaignRow> Campaigns => Set<CampaignRow>();
    public DbSet<CampaignTagRow> CampaignTags => Set<CampaignTagRow>();
    public DbSet<CampaignResourceRow> CampaignResources => Set<CampaignResourceRow>();
    public DbSet<StreamRow> Streams => Set<StreamRow>();
    public DbSet<TaskRow> Tasks => Set<TaskRow>();
    public DbSet<TaskTagRow> TaskTags => Set<TaskTagRow>();
    public DbSet<TaskRewardItemRow> TaskRewardItems => Set<TaskRewardItemRow>();
    public DbSet<MilestoneRow> Milestones => Set<MilestoneRow>();
    public DbSet<ChallengeRow> Challenges => Set<ChallengeRow>();
    public DbSet<BudgetRow> Budgets => Set<BudgetRow>();
    public DbSet<OperationRow> Operations => Set<OperationRow>();
    public DbSet<OperationItemRow> OperationItems => Set<OperationItemRow>();
    public DbSet<ProgressEventRow> ProgressEvents => Set<ProgressEventRow>();
    public DbSet<ProgressStateRow> ProgressStates => Set<ProgressStateRow>();
    public DbSet<CompletionRow> Completions => Set<CompletionRow>();
    public DbSet<StreamPointsRow> StreamPoints => Set<StreamPointsRow>();
    public DbSet<AchievementGrantRow> AchievementGrants => Set<AchievementGrantRow>();
    public DbSet<ChallengeScoreRow> ChallengeScores => Set<ChallengeScoreRow>();
    public DbSet<ChallengeResultRow> ChallengeResults => Set<ChallengeResultRow>();
    public DbSet<ExportRequestRow> ExportRequests => Set<ExportRequestRow>();
    public DbSet<ExportOperationIdRow> ExportOperationIds => Set<ExportOperationIdRow>();
    public DbSet<DownloadLinkRow> DownloadLinks => Set<DownloadLinkRow>();
    public DbSet<IdempotencyKeyRow> IdempotencyKeys => Set<IdempotencyKeyRow>();
    public DbSet<OutboxJobRow> OutboxJobs => Set<OutboxJobRow>();
    public DbSet<AuditRecordRow> AuditRecords => Set<AuditRecordRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CompanyRow>(e =>
        {
            e.ToTable("companies");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<EmployeeRow>(e =>
        {
            e.ToTable("employees");
            e.HasKey(x => new { x.CompanyId, x.MasterId });
            e.Property(x => x.CreatedAt);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(t => new { t.CompanyId, t.MasterId });
        });
        modelBuilder.Entity<EmployeeTagRow>(e =>
        {
            e.ToTable("employee_tags");
            e.HasKey(x => new { x.CompanyId, x.MasterId, x.Tag });
            e.Property(x => x.Tag).HasColumnName("tag");
        });

        modelBuilder.Entity<WalletRow>(e =>
        {
            e.ToTable("wallets");
            e.HasKey(x => new { x.CompanyId, x.MasterId });
            e.Property(x => x.CreatedAt);
        });
        modelBuilder.Entity<WalletBalanceRow>(e =>
        {
            e.ToTable("wallet_balances");
            e.HasKey(x => new { x.CompanyId, x.MasterId, x.ResourceId });
        });

        modelBuilder.Entity<ResourceRow>(e =>
        {
            e.ToTable("resources");
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatedAt);
            e.HasIndex(x => new { x.CompanyId, x.CodeNorm }).IsUnique();
        });

        modelBuilder.Entity<AchievementRow>(e =>
        {
            e.ToTable("achievements");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CompanyId, x.CodeNorm }).IsUnique();
        });

        modelBuilder.Entity<PurchaseSystemRow>(e =>
        {
            e.ToTable("purchase_systems");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CompanyId, x.CodeNorm }).IsUnique();
            e.HasMany(x => x.AcceptedResources).WithOne().HasForeignKey(r => r.PurchaseSystemId);
        });
        modelBuilder.Entity<PurchaseSystemResourceRow>(e =>
        {
            e.ToTable("purchase_system_resources");
            e.HasKey(x => new { x.PurchaseSystemId, x.ResourceId });
        });

        modelBuilder.Entity<IntegrationGrantRow>(e =>
        {
            e.ToTable("integration_grants");
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatedAt);
            e.Property(x => x.RevokedAt);
            e.HasIndex(x => new { x.CompanyId, x.Subject, x.Kind, x.CampaignId, x.ResourceId, x.PurchaseSystemId });
        });

        modelBuilder.Entity<CampaignRow>(e =>
        {
            e.ToTable("campaigns");
            e.HasKey(x => x.Id);
            e.Property(x => x.StartsAt);
            e.Property(x => x.EndsAt);
            e.Property(x => x.CreatedAt);
            e.Property(x => x.PublishedAt);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(t => t.CampaignId);
        });
        modelBuilder.Entity<CampaignTagRow>(e =>
        {
            e.ToTable("campaign_tags");
            e.HasKey(x => new { x.CampaignId, x.Kind, x.Tag });
        });
        modelBuilder.Entity<CampaignResourceRow>(e =>
        {
            e.ToTable("campaign_resources");
            e.HasKey(x => new { x.CampaignId, x.ResourceId });
        });

        modelBuilder.Entity<StreamRow>(e =>
        {
            e.ToTable("streams");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.CampaignId, x.CodeNorm }).IsUnique();
        });

        modelBuilder.Entity<TaskRow>(e =>
        {
            e.ToTable("tasks");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.StreamId, x.CodeNorm }).IsUnique();
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(t => t.TaskId);
            e.HasMany(x => x.RewardItems).WithOne().HasForeignKey(i => i.TaskId);
        });
        modelBuilder.Entity<TaskTagRow>(e =>
        {
            e.ToTable("task_tags");
            e.HasKey(x => new { x.TaskId, x.Kind, x.Tag });
        });
        modelBuilder.Entity<TaskRewardItemRow>(e =>
        {
            e.ToTable("task_reward_items");
            e.HasKey(x => new { x.TaskId, x.ResourceId });
        });

        modelBuilder.Entity<MilestoneRow>(e =>
        {
            e.ToTable("milestones");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.StreamId, x.Threshold, x.AchievementId }).IsUnique();
        });

        modelBuilder.Entity<ChallengeRow>(e =>
        {
            e.ToTable("challenges");
            e.HasKey(x => x.Id);
            e.Property(x => x.StartsAt);
            e.Property(x => x.EndsAt);
            e.Property(x => x.FinalizedAt);
        });

        modelBuilder.Entity<BudgetRow>(e =>
        {
            e.ToTable("budgets");
            e.HasKey(x => new { x.CampaignId, x.ResourceId });
        });

        modelBuilder.Entity<OperationRow>(e =>
        {
            e.ToTable("operations");
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatedAt);
            e.HasIndex(x => new { x.CompanyId, x.InitiatorKey, x.Kind, x.SourceNumber }).IsUnique();
            e.HasIndex(x => x.CompanyId).IncludeProperties(x => new { x.MasterId, x.CreatedAt });
            e.HasIndex(x => x.CampaignId);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.OperationId);
        });
        modelBuilder.Entity<OperationItemRow>(e =>
        {
            e.ToTable("operation_items");
            e.HasKey(x => new { x.OperationId, x.ResourceId });
        });

        modelBuilder.Entity<ProgressEventRow>(e =>
        {
            e.ToTable("progress_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.AcceptedAt);
            e.Property(x => x.PeriodStart);
            e.HasIndex(x => new { x.CompanyId, x.SourceSubject, x.EventNumber }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.MasterId, x.AcceptedAt });
        });

        modelBuilder.Entity<ProgressStateRow>(e =>
        {
            e.ToTable("progress_state");
            e.HasKey(x => new { x.CompanyId, x.MasterId, x.TaskId, x.PeriodStart });
            e.Property(x => x.PeriodStart);
        });

        modelBuilder.Entity<CompletionRow>(e =>
        {
            e.ToTable("completions");
            e.HasKey(x => x.Id);
            e.Property(x => x.PeriodStart);
            e.Property(x => x.CompletedAt);
            e.HasIndex(x => new { x.CompanyId, x.MasterId, x.TaskId, x.PeriodStart }).IsUnique();
        });

        modelBuilder.Entity<StreamPointsRow>(e =>
        {
            e.ToTable("stream_points");
            e.HasKey(x => new { x.CompanyId, x.MasterId, x.StreamId, x.Season });
        });

        modelBuilder.Entity<AchievementGrantRow>(e =>
        {
            e.ToTable("achievement_grants");
            e.HasKey(x => new { x.CompanyId, x.MasterId, x.AchievementId, x.Season });
            e.Property(x => x.GrantedAt);
        });

        modelBuilder.Entity<ChallengeScoreRow>(e =>
        {
            e.ToTable("challenge_scores");
            e.HasKey(x => new { x.ChallengeId, x.MasterId });
        });
        modelBuilder.Entity<ChallengeResultRow>(e =>
        {
            e.ToTable("challenge_results");
            e.HasKey(x => new { x.ChallengeId, x.MasterId });
            e.Property(x => x.FinalizedAt);
        });

        modelBuilder.Entity<ExportRequestRow>(e =>
        {
            e.ToTable("export_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.S3Key).HasColumnName("s3_key");
            e.Property(x => x.S3Version).HasColumnName("s3_version");
            e.Property(x => x.FromUtc);
            e.Property(x => x.ToUtc);
            e.Property(x => x.FrozenAt);
            e.Property(x => x.ReadyAt);
            e.Property(x => x.CreatedAt);
            e.Property(x => x.LeaseUntil);
        });
        modelBuilder.Entity<ExportOperationIdRow>(e =>
        {
            e.ToTable("export_operation_ids");
            e.HasKey(x => new { x.ExportId, x.OperationId });
        });
        modelBuilder.Entity<DownloadLinkRow>(e =>
        {
            e.ToTable("download_links");
            e.HasKey(x => x.Id);
            e.Property(x => x.ExpiresAt);
            e.Property(x => x.CreatedAt);
        });

        modelBuilder.Entity<IdempotencyKeyRow>(e =>
        {
            e.ToTable("idempotency_keys");
            e.HasKey(x => new { x.CompanyId, x.InitiatorKey, x.Operation, x.TargetId, x.Key });
            e.Property(x => x.CreatedAt);
        });

        modelBuilder.Entity<OutboxJobRow>(e =>
        {
            e.ToTable("outbox_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.AvailableAt);
            e.Property(x => x.LockedUntil);
            e.Property(x => x.CreatedAt);
            e.HasIndex(x => new { x.Status, x.AvailableAt });
        });

        modelBuilder.Entity<AuditRecordRow>(e =>
        {
            e.ToTable("audit_records");
            e.HasKey(x => x.Id);
            e.Property(x => x.ChangesJson).HasColumnType("jsonb");
            e.Property(x => x.CreatedAt);
            e.HasIndex(x => new { x.CompanyId, x.CreatedAt });
        });
    }
}
