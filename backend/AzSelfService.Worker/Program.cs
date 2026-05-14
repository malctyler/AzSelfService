using AzSelfService.Worker.Data;
using AzSelfService.Worker.Services;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<WorkerOptions>(options =>
{
    options.PollIntervalMs = builder.Configuration.GetValue<int?>("WORKER_POLL_INTERVAL_MS") ?? 5000;
    options.MaxRetries = builder.Configuration.GetValue<int?>("WORKER_MAX_RETRIES") ?? 3;
    options.BatchSize = builder.Configuration.GetValue<int?>("WORKER_BATCH_SIZE") ?? 5;
    options.SecretExpiryWarningDays = builder.Configuration.GetValue<int?>("WORKER_SECRET_EXPIRY_WARNING_DAYS") ?? 30;
    options.TerraformExecutionMode = builder.Configuration["WORKER_TERRAFORM_EXECUTION_MODE"] ?? "simulate";
    options.TerraformBinaryPath = builder.Configuration["WORKER_TERRAFORM_BINARY_PATH"] ?? "terraform";
    options.RepositoryRootPath = builder.Configuration["WORKER_REPOSITORY_ROOT_PATH"] ?? "/app";
    options.TerraformWorkingDirectory = builder.Configuration["WORKER_TERRAFORM_WORKING_DIR"] ?? "/tmp/terraform";
    options.AzureStorageAccountName = builder.Configuration["Azure:Storage:AccountName"] ?? "";
    options.AzureStorageContainerName = builder.Configuration["Azure:Storage:ContainerName"] ?? "customer-tfstate";
});

builder.Services.AddSingleton(_ =>
{
    var keyVaultUrl = builder.Configuration["Azure:KeyVault:Url"];

    if (string.IsNullOrWhiteSpace(keyVaultUrl))
    {
        throw new InvalidOperationException("Azure:KeyVault:Url must be configured for worker credential resolution.");
    }

    return new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential());
});

builder.Services.AddSingleton<ServicePrincipalCredentialProvider>();
builder.Services.AddSingleton<TerraformExecutionService>();

builder.Services.AddDbContext<WorkerDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHostedService<DeploymentProcessor>();

var host = builder.Build();
await host.RunAsync();
