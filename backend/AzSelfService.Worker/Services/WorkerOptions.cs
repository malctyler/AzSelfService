namespace AzSelfService.Worker.Services;

public sealed class WorkerOptions
{
    public int PollIntervalMs { get; set; } = 5000;
    public int MaxRetries { get; set; } = 3;
    public int BatchSize { get; set; } = 5;
    public int SecretExpiryWarningDays { get; set; } = 30;
}