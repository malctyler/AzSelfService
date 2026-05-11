namespace AzSelfService.Worker.Data.Entities;

public sealed class DeploymentLogEntity
{
    public long Id { get; set; }
    public Guid DeploymentId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Level { get; set; } = "INFO";
    public string Message { get; set; } = string.Empty;
    public string? Context { get; set; }
}