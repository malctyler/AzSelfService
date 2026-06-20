using System.Text.Json;

namespace AzSelfService.API.Contracts;

public sealed class CreateDeploymentRequest
{
    public Guid ModuleId { get; set; }
    public JsonElement Inputs { get; set; }
}

public sealed class RetryDeploymentRequest
{
    /// <summary>
    /// Corrected inputs for the retry. Must include all required fields.
    /// The original inputs from the failed deployment are used as a starting
    /// point — only the fields the user changed need to differ.
    /// </summary>
    public JsonElement Inputs { get; set; }
}

public sealed class ImportDeploymentRequest
{
    public Guid ModuleId { get; set; }
    public string ResourceName { get; set; } = string.Empty;
    public string ParentResourceName { get; set; } = string.Empty;
    // resource-group
    public string ResourceGroupName { get; set; } = string.Empty;
    public string Environment { get; set; } = "dev";
    // storage-account
    public string StorageAccountName { get; set; } = string.Empty;
    public string StorageAccountResourceGroup { get; set; } = string.Empty;
    // keyvault
    public string KeyVaultName { get; set; } = string.Empty;
    public string KeyVaultResourceGroup { get; set; } = string.Empty;
    // virtual-network
    public string VirtualNetworkName { get; set; } = string.Empty;
    public string VirtualNetworkResourceGroup { get; set; } = string.Empty;
}

public sealed class ArmLookupResponse
{
    public string ResourceId { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public Dictionary<string, string> ExistingTags { get; init; } = new();
}

public sealed class ImportResourceOptionResponse
{
    public string Name { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public Dictionary<string, string> ExistingTags { get; init; } = new();
    public string Summary { get; init; } = string.Empty;
    public string? ParentName { get; init; }
}

public sealed class StorageAccountSummaryResponse
{
    public string Name { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public Dictionary<string, string> ExistingTags { get; init; } = new();
}

public sealed class KeyVaultSummaryResponse
{
    public string Name { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public Dictionary<string, string> ExistingTags { get; init; } = new();
}

public sealed class VirtualNetworkSubnetInfo
{
    public string Name { get; init; } = string.Empty;
    public string SubnetId { get; init; } = string.Empty;
    public string AddressPrefix { get; init; } = string.Empty;
}

public sealed class VirtualNetworkSummaryResponse
{
    public string Name { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string AddressSpace { get; init; } = string.Empty;
    public IReadOnlyList<VirtualNetworkSubnetInfo> Subnets { get; init; } = Array.Empty<VirtualNetworkSubnetInfo>();
    public Dictionary<string, string> ExistingTags { get; init; } = new();
}

public sealed class DeploymentCreatedResponse
{
    public Guid Id { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class RebuildDeploymentResponse
{
    public Guid DestroyDeploymentId { get; init; }
    public Guid RedeployDeploymentId { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
}

public sealed class RebuildAllResponse
{
    public Guid BatchId { get; init; }
    public int DeploymentCount { get; init; }
    public int DestroyCount { get; init; }
    public int RedeployCount { get; init; }
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