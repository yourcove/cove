using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cove.Data.Configuration;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Username).IsRequired().HasMaxLength(200);
        builder.Property(u => u.DisplayName).HasMaxLength(200);
        builder.Property(u => u.Email).HasMaxLength(320);
        builder.Property(u => u.PasswordHash).IsRequired().HasMaxLength(500);
        builder.Property(u => u.PasswordAlgo).IsRequired().HasMaxLength(32);
        builder.Property(u => u.LastLoginIp).HasMaxLength(64);
        builder.Property(u => u.TotpSecret).HasMaxLength(200);
        builder.Property(u => u.UiPreferencesJson).HasColumnType("text");

        builder.HasIndex(u => u.Username).IsUnique();
        builder.HasIndex(u => u.Email).IsUnique().HasFilter("\"Email\" IS NOT NULL");

        builder.HasMany(u => u.Roles).WithOne(r => r.User!).HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(u => u.RefreshTokens).WithOne(t => t.User!).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(u => u.ApiTokens).WithOne(t => t.User!).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(u => u.ExternalIdentities).WithOne(link => link.User!).HasForeignKey(link => link.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany<UserInviteToken>().WithOne(t => t.User!).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ExternalIdentityLinkConfiguration : IEntityTypeConfiguration<ExternalIdentityLink>
{
    public void Configure(EntityTypeBuilder<ExternalIdentityLink> builder)
    {
        builder.ToTable("external_identity_links");
        builder.HasKey(link => link.Id);
        builder.Property(link => link.ExtensionId).IsRequired().HasMaxLength(256);
        builder.Property(link => link.ProviderId).IsRequired().HasMaxLength(512);
        builder.Property(link => link.Subject).IsRequired().HasMaxLength(512);
        builder.Property(link => link.ProviderLabel).IsRequired().HasMaxLength(128);
        builder.Property(link => link.AccountLabel).HasMaxLength(256);
        builder.HasIndex(link => new { link.ExtensionId, link.ProviderId, link.Subject }).IsUnique();
        builder.HasIndex(link => new { link.UserId, link.ProviderLabel });
    }
}

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Description).HasMaxLength(500);
        builder.Property(r => r.Source).IsRequired().HasMaxLength(200);
        builder.HasIndex(r => r.Name).IsUnique();

        builder.HasMany(r => r.Permissions).WithOne(p => p.Role!).HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(r => r.Users).WithOne(u => u.Role!).HasForeignKey(u => u.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(r => r.ContentRules).WithOne(c => c.Role!).HasForeignKey(c => c.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(r => r.EntityOverrides).WithOne(o => o.Role!).HasForeignKey(o => o.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("permissions");
        builder.HasKey(p => p.Key);
        builder.Property(p => p.Key).HasMaxLength(200);
        builder.Property(p => p.Category).IsRequired().HasMaxLength(100);
        builder.Property(p => p.Description).IsRequired().HasMaxLength(1000);
        builder.Property(p => p.Source).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Implies).HasColumnType("jsonb");

        builder.HasIndex(p => p.Source);
    }
}

public class UserRoleAssignmentConfiguration : IEntityTypeConfiguration<UserRoleAssignment>
{
    public void Configure(EntityTypeBuilder<UserRoleAssignment> builder)
    {
        builder.ToTable("user_role_assignments");
        builder.HasKey(x => new { x.UserId, x.RoleId });
        builder.HasOne(x => x.GrantedBy).WithMany().HasForeignKey(x => x.GrantedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");
        builder.HasKey(x => new { x.RoleId, x.PermissionKey });
        builder.Property(x => x.PermissionKey).IsRequired().HasMaxLength(200);
        // No FK to Permission(Key) — permission catalog is rebuilt on startup, so refer-by-string only.
    }
}

public class RoleContentRuleConfiguration : IEntityTypeConfiguration<RoleContentRule>
{
    public void Configure(EntityTypeBuilder<RoleContentRule> builder)
    {
        builder.ToTable("role_content_rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.EntityKind).IsRequired().HasMaxLength(64);
        builder.Property(r => r.Effect).IsRequired().HasMaxLength(16);
        builder.Property(r => r.ScopeKind).IsRequired().HasMaxLength(32);
        builder.Property(r => r.ScopeValue).HasColumnType("jsonb");
        builder.Property(r => r.AppliesTo).IsRequired().HasMaxLength(16);

        builder.HasIndex(r => new { r.RoleId, r.EntityKind, r.AppliesTo });
    }
}

public class RoleEntityOverrideConfiguration : IEntityTypeConfiguration<RoleEntityOverride>
{
    public void Configure(EntityTypeBuilder<RoleEntityOverride> builder)
    {
        builder.ToTable("role_entity_overrides");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.EntityKind).IsRequired().HasMaxLength(64);
        builder.Property(o => o.EntityId).IsRequired().HasMaxLength(64);
        builder.Property(o => o.Effect).IsRequired().HasMaxLength(16);
        builder.Property(o => o.AppliesTo).IsRequired().HasMaxLength(16);

        builder.HasIndex(o => new { o.RoleId, o.EntityKind, o.EntityId, o.AppliesTo }).IsUnique();
        builder.HasIndex(o => new { o.EntityKind, o.EntityId, o.AppliesTo });
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(128);
        builder.Property(t => t.UserAgent).HasMaxLength(500);
        builder.Property(t => t.Ip).HasMaxLength(64);

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.UserId, t.RevokedAt });

        builder.HasOne(t => t.Parent).WithMany().HasForeignKey(t => t.ParentId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class ApiTokenConfiguration : IEntityTypeConfiguration<ApiToken>
{
    public void Configure(EntityTypeBuilder<ApiToken> builder)
    {
        builder.ToTable("api_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(200);
        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(200);
        builder.Property(t => t.Prefix).IsRequired().HasMaxLength(16);
        builder.Property(t => t.ScopePermissions).HasColumnType("jsonb");

        builder.HasIndex(t => t.TokenHash).IsUnique();
    }
}

public class UserInviteTokenConfiguration : IEntityTypeConfiguration<UserInviteToken>
{
    public void Configure(EntityTypeBuilder<UserInviteToken> builder)
    {
        builder.ToTable("user_invite_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(128);
        builder.Property(t => t.Purpose).IsRequired().HasMaxLength(32);
        builder.Property(t => t.Username).HasMaxLength(64);
        builder.Property(t => t.DisplayName).HasMaxLength(200);
        builder.Property(t => t.Email).HasMaxLength(320);
        builder.Property(t => t.RolesJson).HasColumnType("jsonb");

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.UserId, t.Purpose, t.ConsumedAt });

        builder.HasOne(t => t.CreatedBy).WithMany().HasForeignKey(t => t.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class ShareLinkConfiguration : IEntityTypeConfiguration<ShareLink>
{
    public void Configure(EntityTypeBuilder<ShareLink> builder)
    {
        builder.ToTable("share_links");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.TokenHash).IsRequired().HasMaxLength(128);
        builder.Property(s => s.EntityKind).IsRequired().HasMaxLength(64);
        builder.Property(s => s.EntityIds).HasColumnType("jsonb");
        builder.Property(s => s.ContainedEntityIds).HasColumnType("jsonb");
        builder.Property(s => s.PasswordHash).HasMaxLength(500);

        builder.HasIndex(s => s.TokenHash).IsUnique();

        builder.HasOne(s => s.CreatedBy).WithMany().HasForeignKey(s => s.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedOnAdd();
        builder.Property(e => e.ActorKind).IsRequired().HasMaxLength(32);
        builder.Property(e => e.Ip).HasMaxLength(64);
        builder.Property(e => e.UserAgent).HasMaxLength(500);
        builder.Property(e => e.Action).IsRequired().HasMaxLength(100);
        builder.Property(e => e.TargetKind).HasMaxLength(64);
        builder.Property(e => e.TargetId).HasMaxLength(128);
        builder.Property(e => e.Outcome).IsRequired().HasMaxLength(16);
        builder.Property(e => e.Detail).HasColumnType("jsonb");

        builder.HasIndex(e => e.OccurredAt);
        builder.HasIndex(e => new { e.ActorUserId, e.OccurredAt });
        builder.HasIndex(e => new { e.Action, e.OccurredAt });
    }
}
