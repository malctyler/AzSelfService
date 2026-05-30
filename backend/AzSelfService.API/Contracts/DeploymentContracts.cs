using System.Text.Json;

namespace AzSelfService.API.Contracts;

public sealed class CreateDeploymentRequest
{
    public Guid ModuleId { get; set; }
    public JsonElement Inputs { get; set; }
}

public sealed class DeploymentCreatedResponse
{
    public Guid Id { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class DeploymentDetailsResponse
{
    public Guid Id { get; init; }
    public Guid ModuleId { get; init; }
    public string ModuleName { get; init; } = string.Empty;
    public string ModuleVersion { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
    public int RetryCount { get; init; }
    public string? TerraformStatePath { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public JsonElement Inputs { get; init; }
    public JsonElement? Outputs { get; init; }
}

public sealed class ManagedResourceResponse
{
    public Guid DeploymentId { get; init; }
    public Guid ModuleId { get; init; }
    public string ModuleName { get; init; } = string.Empty;
    public string ModuleVersion { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string ResourceName { get; init; } = string.Empty;
    public string ResourceLocation { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string? TerraformStatePath { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
}

public sealed class DeploymentLogResponse
{
    public long Id { get; init; }
    public DateTime TimestampUtc { get; init; }
    public string Level { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public JsonElement? Context { get; init; }
}