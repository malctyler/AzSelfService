using AzSelfService.API.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Data;

public sealed class AzSelfServiceDbContext(DbContextOptions<AzSelfServiceDbContext> options) : DbContext(options)
{
    public DbSet<CustomerEntity> Customers => Set<CustomerEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<ModuleEntity> Modules => Set<ModuleEntity>();
    public DbSet<AllowedRegionEntity> AllowedRegions => Set<AllowedRegionEntity>();
    public DbSet<DeploymentEntity> Deployments => Set<DeploymentEntity>();
    public DbSet<DeploymentInputEntity> DeploymentInputs => Set<DeploymentInputEntity>();
    public DbSet<DeploymentOutputEntity> DeploymentOutputs => Set<DeploymentOutputEntity>();
    public DbSet<DeploymentLogEntity> DeploymentLogs => Set<DeploymentLogEntity>();
    public DbSet<StorageAccount> StorageAccounts => Set<StorageAccount>();
    public DbSet<SoftwarePackageEntity> SoftwarePackages => Set<SoftwarePackageEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomerEntity>(entity =>
        {
            entity.ToTable("customers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(255);
            entity.Property(x => x.SubscriptionId).HasColumnName("subscription_id").HasMaxLength(255);
            entity.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(255);
            entity.Property(x => x.SpClientIdSecretRef).HasColumnName("sp_client_id_secret_ref").HasMaxLength(1024);
            entity.Property(x => x.SpClientSecretSecretRef).HasColumnName("sp_client_secret_secret_ref").HasMaxLength(1024);
            entity.Property(x => x.SpTenantIdSecretRef).HasColumnName("sp_tenant_id_secret_ref").HasMaxLength(1024);
            entity.Property(x => x.SpSubscriptionIdSecretRef).HasColumnName("sp_subscription_id_secret_ref").HasMaxLength(1024);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.IsActive).HasColumnName("is_active");
        });

        modelBuilder.Entity<UserEntity>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CustomerId).HasColumnName("customer_id");
            entity.Property(x => x.Username).HasColumnName("username").HasMaxLength(255);
            entity.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(255);
            entity.Property(x => x.Email).HasColumnName("email").HasMaxLength(255);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.IsActive).HasColumnName("is_active");

            entity.HasOne(x => x.Customer)
                .WithMany(x => x.Users)
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModuleEntity>(entity =>
        {
            entity.ToTable("modules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(255);
            entity.Property(x => x.Version).HasColumnName("version").HasMaxLength(50);
            entity.Property(x => x.TerraformPath).HasColumnName("terraform_path").HasMaxLength(512);
            entity.Property(x => x.Schema).HasColumnName("schema").HasColumnType("jsonb");
            entity.Property(x => x.UiSchema).HasColumnName("ui_schema").HasColumnType("jsonb");
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.IsPublished).HasColumnName("is_published");
            entity.Property(x => x.IsDeprecated).HasColumnName("is_deprecated");
        });

        modelBuilder.Entity<AllowedRegionEntity>(entity =>
        {
            entity.ToTable("allowed_regions");
            entity.HasKey(x => x.Code);
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(64);
            entity.Property(x => x.SortOrder).HasColumnName("sort_order");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<DeploymentEntity>(entity =>
        {
            entity.ToTable("deployments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.CustomerId).HasColumnName("customer_id");
            entity.Property(x => x.ModuleId).HasColumnName("module_id");
            entity.Property(x => x.RequestedBy).HasColumnName("requested_by");
            entity.Property(x => x.Status).HasColumnName("status").HasMaxLength(50);
            entity.Property(x => x.ErrorMessage).HasColumnName("error_message");
            entity.Property(x => x.RetryCount).HasColumnName("retry_count");
            entity.Property(x => x.TerraformStatePath).HasColumnName("terraform_state_path").HasMaxLength(512);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");

            entity.HasOne(x => x.Customer)
                .WithMany(x => x.Deployments)
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Module)
                .WithMany(x => x.Deployments)
                .HasForeignKey(x => x.ModuleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.RequestedByUser)
                .WithMany(x => x.RequestedDeployments)
                .HasForeignKey(x => x.RequestedBy)
                .OnDelete(DeleteBehavior.ClientSetNull);
        });

        modelBuilder.Entity<DeploymentInputEntity>(entity =>
        {
            entity.ToTable("deployment_inputs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            entity.Property(x => x.Inputs).HasColumnName("inputs").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");

            entity.HasOne(x => x.Deployment)
                .WithOne(x => x.Input)
                .HasForeignKey<DeploymentInputEntity>(x => x.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeploymentOutputEntity>(entity =>
        {
            entity.ToTable("deployment_outputs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            entity.Property(x => x.Outputs).HasColumnName("outputs").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");

            entity.HasOne(x => x.Deployment)
                .WithOne(x => x.Output)
                .HasForeignKey<DeploymentOutputEntity>(x => x.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeploymentLogEntity>(entity =>
        {
            entity.ToTable("deployment_logs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            entity.Property(x => x.Timestamp).HasColumnName("timestamp");
            entity.Property(x => x.Level).HasColumnName("level").HasMaxLength(20);
            entity.Property(x => x.Message).HasColumnName("message");
            entity.Property(x => x.Context).HasColumnName("context").HasColumnType("jsonb");

            entity.HasOne(x => x.Deployment)
                .WithMany(x => x.Logs)
                .HasForeignKey(x => x.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StorageAccount>(entity =>
        {
            entity.ToTable("storage_accounts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name").HasMaxLength(255);
            entity.Property(x => x.Region).HasColumnName("region").HasMaxLength(50);
            entity.Property(x => x.ResourceGroup).HasColumnName("resource_group").HasMaxLength(255);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<SoftwarePackageEntity>(entity =>
        {
            entity.ToTable("software_packages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(32);
            entity.Property(x => x.CustomerId).HasColumnName("customer_id");
            entity.Property(x => x.PackageId).HasColumnName("package_id").HasMaxLength(255);
            entity.Property(x => x.Version).HasColumnName("version").HasMaxLength(50);
            entity.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(255);
            entity.Property(x => x.Publisher).HasColumnName("publisher").HasMaxLength(255);
            entity.Property(x => x.Os).HasColumnName("os").HasMaxLength(64);
            entity.Property(x => x.Architecture).HasColumnName("architecture").HasMaxLength(64);
            entity.Property(x => x.InstallerType).HasColumnName("installer_type").HasMaxLength(64);
            entity.Property(x => x.BlobPath).HasColumnName("blob_path").HasMaxLength(1024);
            entity.Property(x => x.ZipSha256).HasColumnName("zip_sha256").HasMaxLength(64);
            entity.Property(x => x.ManifestJson).HasColumnName("manifest_json").HasColumnType("jsonb");
            entity.Property(x => x.IsPublished).HasColumnName("is_published");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");

            entity.HasIndex(x => new { x.Scope, x.CustomerId, x.PackageId, x.Version })
                .IsUnique();

            entity.HasOne(x => x.Customer)
                .WithMany()
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}