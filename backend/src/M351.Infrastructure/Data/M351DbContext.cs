using M351.Domain;
using M351.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace M351.Infrastructure.Data;

/// <summary>
/// DbContext da F0. Mapeia apenas as tabelas que a F0 usa; as demais tabelas da Seção 7.1
/// existem somente na migration inicial (serão mapeadas nas fases seguintes).
/// Filtro global por tenant em TODAS as entidades de dados + interceptor que carimba tenant_id.
/// </summary>
public class M351DbContext(DbContextOptions<M351DbContext> options, TenantContext tenantContext)
    : DbContext(options)
{
    private readonly TenantContext _tenant = tenantContext;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<EnrollmentKey> EnrollmentKeys => Set<EnrollmentKey>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();
    public DbSet<TenantAgentConfig> TenantAgentConfigs => Set<TenantAgentConfig>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();
    public DbSet<UserEmailPrefs> UserEmailPrefs => Set<UserEmailPrefs>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("citext");

        modelBuilder.Entity<Organization>(e =>
        {
            e.ToTable("organizations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnName("name").HasColumnType("text");
            e.Property(x => x.Slug).HasColumnName("slug").HasColumnType("text");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Timezone).HasColumnName("timezone").HasColumnType("text");
            e.Property(x => x.BusinessHours).HasColumnName("business_hours").HasColumnType("jsonb");
            e.Property(x => x.Plan).HasColumnName("plan").HasColumnType("text");
            e.Property(x => x.DeviceLimit).HasColumnName("device_limit");
            e.Property(x => x.Status).HasColumnName("status").HasColumnType("text");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            // F4.8 — transparência editável (Seção 8.8)
            e.Property(x => x.FinalidadeDeclarada).HasColumnName("finalidade_declarada").HasColumnType("text");
            e.Property(x => x.ContatoDpo).HasColumnName("contato_dpo").HasColumnType("text");
            e.Property(x => x.DataVigencia).HasColumnName("data_vigencia").HasColumnType("date");
            // F5 — checklist de primeiros passos (Seção 8.3 passo 4)
            e.Property(x => x.OnboardingChecklistDismissedAt).HasColumnName("onboarding_checklist_dismissed_at");
            // F5 — idempotência do digest semanal
            e.Property(x => x.LastWeeklyDigestAt).HasColumnName("last_weekly_digest_at");
            // F5 — metas semanais AGREGADAS da organização (nunca por pessoa)
            e.Property(x => x.GoalWeeklyActiveHours).HasColumnName("goal_weekly_active_hours");
            e.Property(x => x.GoalWorkRelatedPct).HasColumnName("goal_work_related_pct");

            // a organização É o tenant: visível apenas para o próprio tenant autenticado
            e.HasQueryFilter(x => _tenant.TenantId != null && x.Id == _tenant.TenantId.Value);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Email).HasColumnName("email").HasColumnType("citext");
            e.Property(x => x.PasswordHash).HasColumnName("password_hash").HasColumnType("text");
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasColumnType("text");
            e.Property(x => x.Role).HasColumnName("role").HasColumnType("text")
                .HasConversion(r => r.ToDbValue(), v => UserRoleExtensions.FromDbValue(v));
            e.Property(x => x.MfaSecretEnc).HasColumnName("mfa_secret_enc");
            e.Property(x => x.MfaEnabled).HasColumnName("mfa_enabled");
            e.Property(x => x.FailedLoginCount).HasColumnName("failed_login_count");
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.Property(x => x.Status).HasColumnName("status").HasColumnType("text");
            e.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
            e.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();

            e.HasQueryFilter(x => _tenant.TenantId != null && x.TenantId == _tenant.TenantId.Value);
        });

        modelBuilder.Entity<Invitation>(e =>
        {
            e.ToTable("invitations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Email).HasColumnName("email").HasColumnType("citext");
            e.Property(x => x.Role).HasColumnName("role").HasColumnType("text")
                .HasConversion(r => r.ToDbValue(), v => UserRoleExtensions.FromDbValue(v));
            e.Property(x => x.TokenHash).HasColumnName("token_hash");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.AcceptedAt).HasColumnName("accepted_at");
            e.Property(x => x.InvitedBy).HasColumnName("invited_by");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.InvitedBy);

            e.HasQueryFilter(x => _tenant.TenantId != null && x.TenantId == _tenant.TenantId.Value);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.UserAgent).HasColumnName("user_agent").HasColumnType("text");
            e.Property(x => x.Ip).HasColumnName("ip").HasColumnType("inet");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.TokenHash);

            e.HasQueryFilter(x => _tenant.TenantId != null && x.TenantId == _tenant.TenantId.Value);
        });

        modelBuilder.Entity<UserEmailPrefs>(e =>
        {
            e.ToTable("user_email_prefs");
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasColumnName("user_id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.WeeklyDigest).HasColumnName("weekly_digest");
            e.Property(x => x.FleetAlerts).HasColumnName("fleet_alerts");
            e.Property(x => x.JornadaWeekly).HasColumnName("jornada_weekly");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasOne<User>().WithOne().HasForeignKey<UserEmailPrefs>(x => x.UserId);

            e.HasQueryFilter(x => _tenant.TenantId != null && x.TenantId == _tenant.TenantId.Value);
        });

        modelBuilder.Entity<PasswordResetToken>(e =>
        {
            e.ToTable("password_reset_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.UsedAt).HasColumnName("used_at");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.TokenHash);

            e.HasQueryFilter(x => _tenant.TenantId != null && x.TenantId == _tenant.TenantId.Value);
        });

        modelBuilder.Entity<MfaRecoveryCode>(e =>
        {
            e.ToTable("mfa_recovery_codes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.CodeHash).HasColumnName("code_hash");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UsedAt).HasColumnName("used_at");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => x.UserId);

            e.HasQueryFilter(x => _tenant.TenantId != null && x.TenantId == _tenant.TenantId.Value);
        });

        modelBuilder.Entity<EnrollmentKey>(e =>
        {
            e.ToTable("enrollment_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.KeyPrefix).HasColumnName("key_prefix").HasColumnType("text");
            e.Property(x => x.KeyHash).HasColumnName("key_hash");
            e.Property(x => x.Label).HasColumnName("label").HasColumnType("text");
            e.Property(x => x.MaxUses).HasColumnName("max_uses");
            e.Property(x => x.UseCount).HasColumnName("use_count");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");

            e.HasQueryFilter(x => _tenant.TenantId != null && x.TenantId == _tenant.TenantId.Value);
        });

        modelBuilder.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Hostname).HasColumnName("hostname").HasColumnType("text");
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasColumnType("text");
            e.Property(x => x.MachineFingerprint).HasColumnName("machine_fingerprint").HasColumnType("text");
            e.Property(x => x.OsVersion).HasColumnName("os_version").HasColumnType("text");
            e.Property(x => x.OsType).HasColumnName("os_type").HasColumnType("text");
            e.Property(x => x.AgentVersion).HasColumnName("agent_version").HasColumnType("text");
            e.Property(x => x.EnrollmentKeyId).HasColumnName("enrollment_key_id");
            e.Property(x => x.TokenHash).HasColumnName("token_hash");
            e.Property(x => x.ConfigVersion).HasColumnName("config_version");
            e.Property(x => x.Tags).HasColumnName("tags").HasColumnType("text[]");
            e.Property(x => x.Status).HasColumnName("status").HasColumnType("text");
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(x => x.ClockOffsetMs).HasColumnName("clock_offset_ms");
            e.Property(x => x.TzOffsetMin).HasColumnName("tz_offset_min");
            e.Property(x => x.TzIana).HasColumnName("tz_iana").HasColumnType("text");
            e.Property(x => x.SeqMax).HasColumnName("seq_max");
            e.Property(x => x.NoticeAckedAt).HasColumnName("notice_acked_at");
            e.Property(x => x.LastTamperAt).HasColumnName("last_tamper_at");
            e.Property(x => x.LastTamperReason).HasColumnName("last_tamper_reason").HasColumnType("text");
            // F5 — página pública do funcionário por device
            e.Property(x => x.TransparencyToken).HasColumnName("transparency_token");
            e.HasIndex(x => x.TransparencyToken).IsUnique();
            e.HasOne<EnrollmentKey>().WithMany().HasForeignKey(x => x.EnrollmentKeyId);
            e.HasIndex(x => new { x.TenantId, x.MachineFingerprint }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.LastSeenAt }).HasDatabaseName("ix_devices_tenant_lastseen");

            e.HasQueryFilter(x => _tenant.TenantId != null && x.TenantId == _tenant.TenantId.Value);
        });

        modelBuilder.Entity<DeviceCommand>(e =>
        {
            e.ToTable("device_commands");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.DeviceId).HasColumnName("device_id");
            e.Property(x => x.Type).HasColumnName("type").HasColumnType("text");
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.DeliveredAt).HasColumnName("delivered_at");

            e.HasQueryFilter(x => _tenant.TenantId != null && x.TenantId == _tenant.TenantId.Value);
        });

        modelBuilder.Entity<TenantAgentConfig>(e =>
        {
            e.ToTable("tenant_agent_configs");
            e.HasKey(x => x.TenantId);
            e.Property(x => x.TenantId).HasColumnName("tenant_id").ValueGeneratedNever();
            e.Property(x => x.ConfigVersion).HasColumnName("config_version");
            e.Property(x => x.HeartbeatSec).HasColumnName("heartbeat_sec");
            e.Property(x => x.ActiveWindowPollSec).HasColumnName("active_window_poll_sec");
            e.Property(x => x.IdleThresholdSec).HasColumnName("idle_threshold_sec");
            e.Property(x => x.WindowTitlePolicy).HasColumnName("window_title_policy").HasColumnType("text");
            e.Property(x => x.MaskedPatterns).HasColumnName("masked_patterns").HasColumnType("text[]");
            e.Property(x => x.IgnoredProcesses).HasColumnName("ignored_processes").HasColumnType("text[]");
            e.Property(x => x.CollectionWindow).HasColumnName("collection_window").HasColumnType("jsonb");
            // F5 — aviso de ciência gerenciado pelo tenant (viaja na config do ack)
            e.Property(x => x.NoticeText).HasColumnName("notice_text").HasColumnType("text");
            e.Property(x => x.NoticeVersion).HasColumnName("notice_version");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            e.HasQueryFilter(x => _tenant.TenantId != null && x.TenantId == _tenant.TenantId.Value);
        });

        modelBuilder.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("audit_log");
            // partição por RANGE (occurred_at) exige occurred_at na PK
            e.HasKey(x => new { x.Id, x.OccurredAt });
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.ActorIp).HasColumnName("actor_ip").HasColumnType("inet");
            e.Property(x => x.Action).HasColumnName("action").HasColumnType("text");
            e.Property(x => x.TargetType).HasColumnName("target_type").HasColumnType("text");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.Detail).HasColumnName("detail").HasColumnType("jsonb");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");

            e.HasQueryFilter(x => _tenant.TenantId != null && x.TenantId == _tenant.TenantId.Value);
        });
    }
}
