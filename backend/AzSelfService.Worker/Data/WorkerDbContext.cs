using AzSelfService.Worker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.Worker.Data;

public sealed class WorkerDbContext(DbContextOptions<WorkerDbContext> options) : DbContext(options)
{
    public DbSet<CustomerEntity> Customers => Set<CustomerEntity>();
    public DbSet<DeploymentEntity> Deployments => Set<DeploymentEntity>();
    public DbSet<ModuleEntity> Modules => Set<ModuleEntity>();
    public DbSet<DeploymentInputEntity> DeploymentInputs => Set<DeploymentInputEntity>();
    public DbSet<DeploymentOutputEntity> DeploymentOutputs => Set<DeploymentOutputEntity>();
    public DbSet<DeploymentLogEntity> DeploymentLogs => Set<DeploymentLogEntity>();

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
            entity.Property(x => x.IsActive).HasColumnName("is_active");
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

            entity.HasOne(x => x.Module)
                .WithMany()
                .HasForeignKey(x => x.ModuleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.Input)
                .WithOne(x => x.Deployment)
                .HasForeignKey<DeploymentInputEntity>(x => x.DeploymentId)
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
            entity.Property(x => x.IsPublished).HasColumnName("is_published");
            entity.Property(x => x.IsDeprecated).HasColumnName("is_deprecated");
        });

        modelBuilder.Entity<DeploymentInputEntity>(entity =>
        {
            entity.ToTable("deployment_inputs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            entity.Property(x => x.Inputs).HasColumnName("inputs").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<DeploymentOutputEntity>(entity =>
        {
            entity.ToTable("deployment_outputs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            entity.Property(x => x.Outputs).HasColumnName("outputs").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
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
        });
    }
}