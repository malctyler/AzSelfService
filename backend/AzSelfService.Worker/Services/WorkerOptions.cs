namespace AzSelfService.Worker.Services;

public sealed class WorkerOptions
{
    public int PollIntervalMs { get; set; } = 5000;
    public int MaxRetries { get; set; } = 3;
    public int BatchSize { get; set; } = 5;
    public int MaxRunningMinutes { get; set; } = 30;
    public int SecretExpiryWarningDays { get; set; } = 30;
    public string TerraformExecutionMode { get; set; } = "simulate";
    public string TerraformBinaryPath { get; set; } = "terraform";
    public string RepositoryRootPath { get; set; } = "/app";
    public string TerraformWorkingDirectory { get; set; } = "/tmp/terraform";
    public string AzureStorageAccountName { get; set; } = "";
    public string AzureStorageContainerName { get; set; } = "customer-tfstate";
    
    // Platform-owned backend credentials for Terraform state access (not customer credentials)
    public string? AzureStorageBackendAccessKey { get; set; }
    public string? AzureStorageBackendSasToken { get; set; }
    public string? AzureStorageBackendSubscriptionId { get; set; }
    public string? AzureStorageBackendResourceGroup { get; set; }
}