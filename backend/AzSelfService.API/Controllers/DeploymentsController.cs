using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using AzSelfService.API.Security;
using AzSelfService.API.Services;
using AzSelfService.API.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class DeploymentsController(
    AzSelfServiceDbContext dbContext,
    CustomerCredentialPreflightService preflightService,
    StorageAccountNameAvailabilityService? storageAccountNameAvailabilityService = null,
    AllowedRegionCatalogService? allowedRegionCatalogService = null,
    ResourceGroupLookupService? resourceGroupLookupService = null,
    ImportResourceDiscoveryService? importResourceDiscoveryService = null) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ManagedResourceResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ManagedResourceResponse>>> GetManagedResources(CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();

        var deployments = await dbContext.Deployments
            .Include(x => x.Module)
            .Include(x => x.Input)
            .Include(x => x.Output)
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.UpdatedAt)
            .ToListAsync(cancellationToken);

        var managedResources = deployments
            .Where(x => !string.IsNullOrWhiteSpace(x.TerraformStatePath))
            .GroupBy(x => NormalizeStatePath(x.TerraformStatePath!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(ToManagedResourceResponse)
            .ToList();

        return Ok(managedResources);
    }

    [HttpPost]
    [ProducesResponseType(typeof(DeploymentCreatedResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeploymentCreatedResponse>> CreateDeployment(
        [FromBody] CreateDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();
        var userId = User.GetRequiredUserId();

        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

        if (customer is null)
        {
            return BadRequest(new { message = "Customer is not active or does not exist." });
        }

        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef)
            || string.IsNullOrWhiteSpace(customer.TenantId)
            || string.IsNullOrWhiteSpace(customer.SubscriptionId))
        {
            return BadRequest(new
            {
                message = "Customer service principal Key Vault secret references are not configured.",
                required = new[]
                {
                    "sp_client_secret_secret_ref",
                    "tenant_id",
                    "subscription_id"
                }
            });
        }

        var module = await dbContext.Modules
            .SingleOrDefaultAsync(x => x.Id == request.ModuleId && x.IsPublished && !x.IsDeprecated, cancellationToken);

        if (module is null)
        {
            return NotFound(new { message = "Module not found." });
        }

        var effectiveInputs = EnsureModuleDefaultInputs(module.Name, request.Inputs, customer);

        var schemaJson = module.Schema;
        if (allowedRegionCatalogService is not null)
        {
            var allowedRegionCodes = await allowedRegionCatalogService.GetAllowedRegionCodesAsync(cancellationToken);
            schemaJson = allowedRegionCatalogService.ApplyAllowedRegionsToSchemaJson(module.Schema, allowedRegionCodes) ?? module.Schema;
        }

        var validationError = ValidateInputsAgainstSchema(schemaJson, effectiveInputs);
        if (validationError is not null)
        {
            return BadRequest(new { message = validationError });
        }

        // Fast-fail for globally unavailable storage account names before full credential preflight.
        if (string.Equals(module.Name, "storage-account", StringComparison.OrdinalIgnoreCase)
            && storageAccountNameAvailabilityService is not null
            && effectiveInputs.TryGetProperty("name", out var storageAccountNameElement)
            && storageAccountNameElement.ValueKind == JsonValueKind.String)
        {
            var storageAccountName = storageAccountNameElement.GetString() ?? string.Empty;
            var nameAvailability = await storageAccountNameAvailabilityService.CheckAsync(
                customer,
                storageAccountName,
                cancellationToken);

            if (!nameAvailability.IsAvailable)
            {
                return BadRequest(new
                {
                    message = nameAvailability.Message ?? "Storage account name is not available globally."
                });
            }
        }

        var preflight = await preflightService.CheckAsync(customer, cancellationToken);
        if (!preflight.CanProceed)
        {
            return BadRequest(new
            {
                message = "Deployment blocked by credential preflight checks.",
                issues = preflight.Issues,
                warnings = preflight.Warnings
            });
        }

        var now = DateTime.UtcNow;
        var terraformStatePath = BuildDeterministicStatePath(customerId, module.Id, module.Name, effectiveInputs);
        var deployment = new DeploymentEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ModuleId = module.Id,
            RequestedBy = userId,
            Status = "QUEUED",
            RetryCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            TerraformStatePath = terraformStatePath
        };

        var input = new DeploymentInputEntity
        {
            Id = Guid.NewGuid(),
            DeploymentId = deployment.Id,
            Inputs = JsonSerializer.Serialize(effectiveInputs),
            CreatedAt = now
        };

        var initialLog = new DeploymentLogEntity
        {
            DeploymentId = deployment.Id,
            Timestamp = now,
            Level = "INFO",
            Message = "Deployment queued.",
            Context = JsonSerializer.Serialize(new { module = module.Name, moduleVersion = module.Version })
        };

        dbContext.Deployments.Add(deployment);
        dbContext.DeploymentInputs.Add(input);
        dbContext.DeploymentLogs.Add(initialLog);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetDeploymentById),
            new { id = deployment.Id },
            new DeploymentCreatedResponse
            {
                Id = deployment.Id,
                Status = deployment.Status,
                CreatedAtUtc = deployment.CreatedAt
            });
    }

    [HttpGet("lookup-resource-group")]
    [ProducesResponseType(typeof(ArmLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ArmLookupResponse>> LookupResourceGroup(
        [FromQuery] string name,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "name query parameter is required." });

        if (resourceGroupLookupService is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Resource group lookup service is not available." });

        var customerId = User.GetRequiredCustomerId();
        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

        if (customer is null)
            return BadRequest(new { message = "Customer is not active or does not exist." });

        var result = await resourceGroupLookupService.LookupAsync(customer, name, cancellationToken);
        if (!result.Found)
            return NotFound(new { message = result.ErrorMessage });

        return Ok(new ArmLookupResponse { ResourceId = result.ResourceId!, Location = result.Location!, ExistingTags = result.ExistingTags });
    }

    [HttpGet("lookup-storage-account")]
    [ProducesResponseType(typeof(ArmLookupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ArmLookupResponse>> LookupStorageAccount(
        [FromQuery] string name,
        [FromQuery] string resourceGroup,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "name query parameter is required." });
        if (string.IsNullOrWhiteSpace(resourceGroup))
            return BadRequest(new { message = "resourceGroup query parameter is required." });

        if (resourceGroupLookupService is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Resource group lookup service is not available." });

        var customerId = User.GetRequiredCustomerId();
        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

        if (customer is null)
            return BadRequest(new { message = "Customer is not active or does not exist." });

        var result = await resourceGroupLookupService.LookupStorageAccountAsync(customer, resourceGroup, name, cancellationToken);
        if (!result.Found)
            return NotFound(new { message = result.ErrorMessage });

        return Ok(new ArmLookupResponse { ResourceId = result.ResourceId!, Location = result.Location!, ExistingTags = result.ExistingTags });
    }

    [HttpGet("list-storage-accounts")]
    [ProducesResponseType(typeof(IReadOnlyList<StorageAccountSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<StorageAccountSummaryResponse>>> ListStorageAccounts(
        [FromQuery] string resourceGroup,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceGroup))
            return BadRequest(new { message = "resourceGroup query parameter is required." });

        if (resourceGroupLookupService is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Resource group lookup service is not available." });

        var customerId = User.GetRequiredCustomerId();
        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

        if (customer is null)
            return BadRequest(new { message = "Customer is not active or does not exist." });

        var (accounts, error) = await resourceGroupLookupService.ListStorageAccountsAsync(customer, resourceGroup, cancellationToken);
        if (accounts is null)
            return BadRequest(new { message = error });

        return Ok(accounts.Select(a => new StorageAccountSummaryResponse
        {
            Name = a.Name,
            ResourceId = a.ResourceId,
            Location = a.Location,
            ExistingTags = a.Tags
        }).ToList());
    }

    [HttpGet("list-key-vaults")]
    [ProducesResponseType(typeof(IReadOnlyList<KeyVaultSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<KeyVaultSummaryResponse>>> ListKeyVaults(
        [FromQuery] string resourceGroup,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceGroup))
            return BadRequest(new { message = "resourceGroup query parameter is required." });

        if (resourceGroupLookupService is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Resource group lookup service is not available." });

        var customerId = User.GetRequiredCustomerId();
        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

        if (customer is null)
            return BadRequest(new { message = "Customer is not active or does not exist." });

        var (vaults, error) = await resourceGroupLookupService.ListKeyVaultsAsync(customer, resourceGroup, cancellationToken);
        if (vaults is null)
            return BadRequest(new { message = error });

        return Ok(vaults.Select(v => new KeyVaultSummaryResponse
        {
            Name = v.Name,
            ResourceId = v.ResourceId,
            Location = v.Location,
            ExistingTags = v.Tags
        }).ToList());
    }

    [HttpGet("list-virtual-networks")]
    [ProducesResponseType(typeof(IReadOnlyList<VirtualNetworkSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<VirtualNetworkSummaryResponse>>> ListVirtualNetworks(
        [FromQuery] string resourceGroup,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceGroup))
            return BadRequest(new { message = "resourceGroup query parameter is required." });

        if (resourceGroupLookupService is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Resource group lookup service is not available." });

        var customerId = User.GetRequiredCustomerId();
        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

        if (customer is null)
            return BadRequest(new { message = "Customer is not active or does not exist." });

        var (vnets, error) = await resourceGroupLookupService.ListVirtualNetworksAsync(customer, resourceGroup, cancellationToken);
        if (vnets is null)
            return BadRequest(new { message = error });

        return Ok(vnets.Select(v => new VirtualNetworkSummaryResponse
        {
            Name = v.Name,
            ResourceId = v.ResourceId,
            Location = v.Location,
            AddressSpace = v.AddressSpace,
            Subnets = v.Subnets.Select(s => new VirtualNetworkSubnetInfo
            {
                Name = s.Name,
                SubnetId = s.SubnetId,
                AddressPrefix = s.AddressPrefix
            }).ToList(),
            ExistingTags = v.Tags
        }).ToList());
    }

    [HttpGet("import-options")]
    [ProducesResponseType(typeof(IReadOnlyList<ImportResourceOptionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ImportResourceOptionResponse>>> ListImportOptions(
        [FromQuery] Guid moduleId,
        [FromQuery] string resourceGroup,
        CancellationToken cancellationToken)
    {
        if (moduleId == Guid.Empty)
            return BadRequest(new { message = "moduleId query parameter is required." });
        if (string.IsNullOrWhiteSpace(resourceGroup))
            return BadRequest(new { message = "resourceGroup query parameter is required." });

        if (importResourceDiscoveryService is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Import resource discovery service is not available." });

        var customerId = User.GetRequiredCustomerId();
        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

        if (customer is null)
            return BadRequest(new { message = "Customer is not active or does not exist." });

        var module = await dbContext.Modules
            .SingleOrDefaultAsync(x => x.Id == moduleId && x.IsPublished && !x.IsDeprecated, cancellationToken);

        if (module is null)
            return NotFound(new { message = "Module not found." });

        if (!IsSupportedForImport(module.Name))
            return BadRequest(new { message = $"Import is not supported for module '{module.Name}'." });

        var managedResourceIds = await GetManagedResourceIdsAsync(customerId, cancellationToken);
        var options = await importResourceDiscoveryService.ListImportOptionsAsync(
            customer,
            module.Name,
            resourceGroup,
            managedResourceIds,
            cancellationToken);

        return Ok(options.Select(x => new ImportResourceOptionResponse
        {
            Name = x.Name,
            ResourceId = x.ResourceId,
            Location = x.Location,
            ExistingTags = x.Tags,
            Summary = x.Summary,
            ParentName = x.ParentName
        }).ToList());
    }

    [HttpPost("import")]
    [ProducesResponseType(typeof(DeploymentCreatedResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeploymentCreatedResponse>> ImportDeployment(
        [FromBody] ImportDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();
        var userId = User.GetRequiredUserId();

        if (resourceGroupLookupService is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Resource group lookup service is not available." });

        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

        if (customer is null)
            return BadRequest(new { message = "Customer is not active or does not exist." });

        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef)
            || string.IsNullOrWhiteSpace(customer.TenantId)
            || string.IsNullOrWhiteSpace(customer.SubscriptionId))
        {
            return BadRequest(new
            {
                message = "Customer service principal Key Vault secret references are not configured.",
                required = new[] { "sp_client_secret_secret_ref", "tenant_id", "subscription_id" }
            });
        }

        var module = await dbContext.Modules
            .SingleOrDefaultAsync(x => x.Id == request.ModuleId && x.IsPublished && !x.IsDeprecated, cancellationToken);

        if (module is null)
            return NotFound(new { message = "Module not found." });

        if (!IsSupportedForImport(module.Name))
            return BadRequest(new { message = $"Import is not supported for module '{module.Name}'." });

        var preflight = await preflightService.CheckAsync(customer, cancellationToken);
        if (!preflight.CanProceed)
        {
            return BadRequest(new
            {
                message = "Import blocked by credential preflight checks.",
                issues = preflight.Issues,
                warnings = preflight.Warnings
            });
        }

        Dictionary<string, object?> resolvedInputs;
        ArmLookupResult lookup;
        Dictionary<string, JsonElement>? overrideInputsWithMeta = null;
        var normalizedModule = module.Name.ToLowerInvariant().Trim();

        if (normalizedModule == "resource-group")
        {
            if (string.IsNullOrWhiteSpace(request.ResourceGroupName))
                return BadRequest(new { message = "resourceGroupName is required." });

            lookup = await resourceGroupLookupService.LookupAsync(customer, request.ResourceGroupName, cancellationToken);
            if (!lookup.Found)
                return NotFound(new { message = lookup.ErrorMessage });

            var environment = string.IsNullOrWhiteSpace(request.Environment) ? "dev" : request.Environment;
            resolvedInputs = new Dictionary<string, object?>
            {
                ["name"] = request.ResourceGroupName,
                ["location"] = lookup.Location,
                ["environment"] = environment,
                ["tags"] = lookup.ExistingTags
            };
        }
        else if (normalizedModule == "storage-account")
        {
            if (string.IsNullOrWhiteSpace(request.StorageAccountName))
                return BadRequest(new { message = "storageAccountName is required." });
            if (string.IsNullOrWhiteSpace(request.StorageAccountResourceGroup))
                return BadRequest(new { message = "storageAccountResourceGroup is required." });

            lookup = await resourceGroupLookupService.LookupStorageAccountAsync(
                customer, request.StorageAccountResourceGroup, request.StorageAccountName, cancellationToken);
            if (!lookup.Found)
                return NotFound(new { message = lookup.ErrorMessage });

            resolvedInputs = new Dictionary<string, object?>
            {
                ["name"] = request.StorageAccountName,
                ["resource_group_name"] = request.StorageAccountResourceGroup,
                ["location"] = lookup.Location,
                ["account_tier"] = "Standard",
                ["account_replication_type"] = "LRS",
                ["tags"] = lookup.ExistingTags
            };
        }
        else if (normalizedModule == "keyvault")
        {
            if (string.IsNullOrWhiteSpace(request.KeyVaultName))
                return BadRequest(new { message = "keyVaultName is required." });
            if (string.IsNullOrWhiteSpace(request.KeyVaultResourceGroup))
                return BadRequest(new { message = "keyVaultResourceGroup is required." });

            lookup = await resourceGroupLookupService.LookupKeyVaultAsync(
                customer, request.KeyVaultResourceGroup, request.KeyVaultName, cancellationToken);
            if (!lookup.Found)
                return NotFound(new { message = lookup.ErrorMessage });

            resolvedInputs = new Dictionary<string, object?>
            {
                ["name"] = request.KeyVaultName,
                ["resource_group_name"] = request.KeyVaultResourceGroup,
                ["location"] = lookup.Location,
                ["tenant_id"] = customer.TenantId,
                ["sku_name"] = "standard",
                ["tags"] = lookup.ExistingTags
            };
        }
        else if (normalizedModule == "virtual-network")
        {
            if (string.IsNullOrWhiteSpace(request.VirtualNetworkName))
                return BadRequest(new { message = "virtualNetworkName is required." });
            if (string.IsNullOrWhiteSpace(request.VirtualNetworkResourceGroup))
                return BadRequest(new { message = "virtualNetworkResourceGroup is required." });

            var vnetInfo = await resourceGroupLookupService.LookupVirtualNetworkAsync(
                customer, request.VirtualNetworkResourceGroup, request.VirtualNetworkName, cancellationToken);

            if (!vnetInfo.Found)
                return NotFound(new { message = vnetInfo.ErrorMessage });

            // Build import blocks for terraform import operation
            var importBlocks = new List<object>
            {
                new { address = "azurerm_virtual_network.this", resourceId = vnetInfo.ResourceId }
            };

            var importedNsgsByName = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // Build explicit subnet and NSG definitions with discovered data
            var subnetsInput = new List<object>();
            for (var i = 0; i < vnetInfo.Subnets.Count; i++)
            {
                var subnet = vnetInfo.Subnets[i];
                importBlocks.Add(new { address = $"azurerm_subnet.this[\"{i}\"]", resourceId = subnet.SubnetId });

                var subnetNsgName = string.Empty;
                if (TryParseResourceIdName(subnet.NetworkSecurityGroupId, "networkSecurityGroups", out var parsedNsgName)
                    && !string.IsNullOrWhiteSpace(parsedNsgName))
                {
                    subnetNsgName = parsedNsgName;

                    if (!importedNsgsByName.ContainsKey(parsedNsgName))
                    {
                        if (importResourceDiscoveryService is not null
                            && TryParseResourceIdResourceGroup(subnet.NetworkSecurityGroupId, out var nsgResourceGroup)
                            && !string.IsNullOrWhiteSpace(nsgResourceGroup))
                        {
                            var nsgInfo = await importResourceDiscoveryService.LookupNetworkSecurityGroupAsync(
                                customer,
                                nsgResourceGroup,
                                parsedNsgName,
                                cancellationToken);

                            if (nsgInfo is not null)
                            {
                                importedNsgsByName[parsedNsgName] = new
                                {
                                    name = nsgInfo.Name,
                                    tags = nsgInfo.Tags,
                                    security_rules = nsgInfo.SecurityRules
                                };

                                importBlocks.Add(new
                                {
                                    address = $"azurerm_network_security_group.explicit[\"{EscapeTerraformAddressKey(parsedNsgName)}\"]",
                                    resourceId = nsgInfo.ResourceId
                                });
                            }
                        }

                        if (!importedNsgsByName.ContainsKey(parsedNsgName))
                        {
                            importedNsgsByName[parsedNsgName] = new
                            {
                                name = parsedNsgName,
                                tags = new Dictionary<string, string>(),
                                security_rules = Array.Empty<object>()
                            };

                            if (!string.IsNullOrWhiteSpace(subnet.NetworkSecurityGroupId))
                            {
                                importBlocks.Add(new
                                {
                                    address = $"azurerm_network_security_group.explicit[\"{EscapeTerraformAddressKey(parsedNsgName)}\"]",
                                    resourceId = subnet.NetworkSecurityGroupId
                                });
                            }
                        }
                    }

                    importBlocks.Add(new
                    {
                        address = $"azurerm_subnet_network_security_group_association.primary[\"{i}\"]",
                        resourceId = subnet.SubnetId
                    });
                }

                subnetsInput.Add(new
                {
                    name = subnet.Name,
                    address_prefix = subnet.AddressPrefix,
                    service_endpoints = subnet.ServiceEndpoints ?? Array.Empty<string>(),
                    network_security_group_name = subnetNsgName,
                    network_security_group_id = string.Empty
                });
            }

            resolvedInputs = new Dictionary<string, object?>
            {
                ["name"] = request.VirtualNetworkName,
                ["resource_group_name"] = request.VirtualNetworkResourceGroup,
                ["location"] = vnetInfo.Location,
                ["address_space"] = vnetInfo.AddressSpace,
                ["subnets"] = subnetsInput,
                ["nsgs"] = importedNsgsByName.Values.ToList(),
                ["dns_servers"] = "",
                ["tags"] = vnetInfo.Tags
            };
            lookup = ArmLookupResult.Success(vnetInfo.ResourceId, vnetInfo.Location, vnetInfo.Tags);

            // Build merged inputs with multi-import blocks and explicit subnet definitions
            var vnetInputsElement = JsonSerializer.SerializeToElement(resolvedInputs);
            var vnetMerged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in vnetInputsElement.EnumerateObject())
                vnetMerged[property.Name] = property.Value.Clone();
            vnetMerged["__operation"] = JsonSerializer.SerializeToElement("import");
            vnetMerged["__import_blocks"] = JsonSerializer.SerializeToElement(importBlocks);
            overrideInputsWithMeta = vnetMerged;
        }
        else if (normalizedModule == "public-ip")
        {
            if (string.IsNullOrWhiteSpace(request.ResourceGroupName))
                return BadRequest(new { message = "resourceGroupName is required." });
            if (string.IsNullOrWhiteSpace(request.ResourceName))
                return BadRequest(new { message = "resourceName is required." });
            if (importResourceDiscoveryService is null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Import resource discovery service is not available." });

            var info = await importResourceDiscoveryService.LookupPublicIpAsync(customer, request.ResourceGroupName, request.ResourceName, cancellationToken)
                ?? throw new InvalidOperationException($"Could not look up public IP '{request.ResourceName}'.");

            lookup = ArmLookupResult.Success(info.ResourceId, info.Location, info.Tags);
            resolvedInputs = new Dictionary<string, object?>
            {
                ["name"] = info.Name,
                ["resource_group_name"] = info.ResourceGroupName,
                ["location"] = info.Location,
                ["allocation_method"] = info.AllocationMethod,
                ["sku"] = info.Sku,
                ["sku_tier"] = info.SkuTier,
                ["ip_version"] = info.IpVersion,
                ["idle_timeout_in_minutes"] = info.IdleTimeoutInMinutes,
                ["ddos_protection_mode"] = info.DdosProtectionMode,
                ["zones"] = info.Zones,
                ["tags"] = info.Tags
            };
        }
        else if (normalizedModule == "local-network-gateway")
        {
            if (string.IsNullOrWhiteSpace(request.ResourceGroupName))
                return BadRequest(new { message = "resourceGroupName is required." });
            if (string.IsNullOrWhiteSpace(request.ResourceName))
                return BadRequest(new { message = "resourceName is required." });
            if (importResourceDiscoveryService is null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Import resource discovery service is not available." });

            var info = await importResourceDiscoveryService.LookupLocalNetworkGatewayAsync(customer, request.ResourceGroupName, request.ResourceName, cancellationToken)
                ?? throw new InvalidOperationException($"Could not look up local network gateway '{request.ResourceName}'.");

            lookup = ArmLookupResult.Success(info.ResourceId, info.Location, info.Tags);
            resolvedInputs = new Dictionary<string, object?>
            {
                ["name"] = info.Name,
                ["resource_group_name"] = info.ResourceGroupName,
                ["location"] = info.Location,
                ["gateway_address"] = info.GatewayAddress,
                ["gateway_fqdn"] = info.GatewayFqdn,
                ["address_space"] = info.AddressSpace,
                ["tags"] = info.Tags
            };
        }
        else if (normalizedModule == "virtual-network-gateway")
        {
            if (string.IsNullOrWhiteSpace(request.ResourceGroupName))
                return BadRequest(new { message = "resourceGroupName is required." });
            if (string.IsNullOrWhiteSpace(request.ResourceName))
                return BadRequest(new { message = "resourceName is required." });
            if (importResourceDiscoveryService is null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Import resource discovery service is not available." });

            var info = await importResourceDiscoveryService.LookupVirtualNetworkGatewayAsync(customer, request.ResourceGroupName, request.ResourceName, cancellationToken)
                ?? throw new InvalidOperationException($"Could not look up virtual network gateway '{request.ResourceName}'.");

            lookup = ArmLookupResult.Success(info.ResourceId, info.Location, info.Tags);
            resolvedInputs = new Dictionary<string, object?>
            {
                ["name"] = info.Name,
                ["resource_group_name"] = info.ResourceGroupName,
                ["location"] = info.Location,
                ["type"] = info.Type,
                ["sku"] = info.Sku,
                ["vpn_type"] = info.VpnType,
                ["generation"] = info.Generation,
                ["active_active"] = info.ActiveActive,
                ["bgp_enabled"] = info.BgpEnabled,
                ["dns_forwarding_enabled"] = info.DnsForwardingEnabled,
                ["remote_vnet_traffic_enabled"] = info.RemoteVnetTrafficEnabled,
                ["private_ip_address_enabled"] = info.PrivateIpAddressEnabled,
                ["ip_sec_replay_protection_enabled"] = info.IpSecReplayProtectionEnabled,
                ["virtual_wan_traffic_enabled"] = info.VirtualWanTrafficEnabled,
                ["bgp_route_translation_for_nat_enabled"] = info.BgpRouteTranslationForNatEnabled,
                ["ip_configuration"] = info.IpConfiguration,
                ["bgp_settings"] = info.BgpSettings,
                ["tags"] = info.Tags
            };
        }
        else if (normalizedModule == "virtual-network-peering")
        {
            if (string.IsNullOrWhiteSpace(request.ResourceGroupName))
                return BadRequest(new { message = "resourceGroupName is required." });
            if (string.IsNullOrWhiteSpace(request.ResourceName))
                return BadRequest(new { message = "resourceName is required." });
            if (string.IsNullOrWhiteSpace(request.ParentResourceName))
                return BadRequest(new { message = "parentResourceName is required." });
            if (importResourceDiscoveryService is null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Import resource discovery service is not available." });

            var info = await importResourceDiscoveryService.LookupVirtualNetworkPeeringAsync(
                customer,
                request.ResourceGroupName,
                request.ParentResourceName,
                request.ResourceName,
                cancellationToken) ?? throw new InvalidOperationException($"Could not look up virtual network peering '{request.ResourceName}'.");

            lookup = ArmLookupResult.Success(info.ResourceId, string.Empty, info.Tags);
            resolvedInputs = new Dictionary<string, object?>
            {
                ["name"] = info.Name,
                ["resource_group_name"] = info.ResourceGroupName,
                ["virtual_network_name"] = info.VirtualNetworkName,
                ["remote_virtual_network_id"] = info.RemoteVirtualNetworkId,
                ["allow_virtual_network_access"] = info.AllowVirtualNetworkAccess,
                ["allow_forwarded_traffic"] = info.AllowForwardedTraffic,
                ["allow_gateway_transit"] = info.AllowGatewayTransit,
                ["use_remote_gateways"] = info.UseRemoteGateways
            };
        }
        else if (normalizedModule == "bastion-host")
        {
            if (string.IsNullOrWhiteSpace(request.ResourceGroupName))
                return BadRequest(new { message = "resourceGroupName is required." });
            if (string.IsNullOrWhiteSpace(request.ResourceName))
                return BadRequest(new { message = "resourceName is required." });
            if (importResourceDiscoveryService is null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Import resource discovery service is not available." });

            var info = await importResourceDiscoveryService.LookupBastionHostAsync(customer, request.ResourceGroupName, request.ResourceName, cancellationToken)
                ?? throw new InvalidOperationException($"Could not look up bastion host '{request.ResourceName}'.");

            lookup = ArmLookupResult.Success(info.ResourceId, info.Location, info.Tags);
            resolvedInputs = new Dictionary<string, object?>
            {
                ["name"] = info.Name,
                ["resource_group_name"] = info.ResourceGroupName,
                ["location"] = info.Location,
                ["sku"] = info.Sku,
                ["virtual_network_id"] = info.VirtualNetworkId,
                ["ip_configuration_name"] = info.IpConfigurationName,
                ["subnet_id"] = info.SubnetId,
                ["public_ip_address_id"] = info.PublicIpAddressId,
                ["scale_units"] = info.ScaleUnits,
                ["copy_paste_enabled"] = info.CopyPasteEnabled,
                ["file_copy_enabled"] = info.FileCopyEnabled,
                ["ip_connect_enabled"] = info.IpConnectEnabled,
                ["kerberos_enabled"] = info.KerberosEnabled,
                ["session_recording_enabled"] = info.SessionRecordingEnabled,
                ["shareable_link_enabled"] = info.ShareableLinkEnabled,
                ["tunneling_enabled"] = info.TunnelingEnabled,
                ["zones"] = info.Zones,
                ["tags"] = info.Tags
            };
        }
        else
        {
            return BadRequest(new { message = $"Import is not supported for module '{module.Name}'." });
        }

        var inputsElement = JsonSerializer.SerializeToElement(resolvedInputs);
        var inputsWithMeta = overrideInputsWithMeta ?? InjectImportMeta(inputsElement, lookup.ResourceId!);

        var now = DateTime.UtcNow;
        var terraformStatePath = BuildDeterministicStatePath(customerId, module.Id, module.Name, inputsElement);
        var deployment = new DeploymentEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ModuleId = module.Id,
            RequestedBy = userId,
            Status = "QUEUED",
            RetryCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            TerraformStatePath = terraformStatePath
        };

        var inputEntity = new DeploymentInputEntity
        {
            Id = Guid.NewGuid(),
            DeploymentId = deployment.Id,
            Inputs = JsonSerializer.Serialize(inputsWithMeta),
            CreatedAt = now
        };

        var initialLog = new DeploymentLogEntity
        {
            DeploymentId = deployment.Id,
            Timestamp = now,
            Level = "INFO",
            Message = "Import queued.",
            Context = JsonSerializer.Serialize(new
            {
                module = module.Name,
                moduleVersion = module.Version,
                resourceId = lookup.ResourceId,
                location = lookup.Location
            })
        };

        dbContext.Deployments.Add(deployment);
        dbContext.DeploymentInputs.Add(inputEntity);
        dbContext.DeploymentLogs.Add(initialLog);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetDeploymentById),
            new { id = deployment.Id },
            new DeploymentCreatedResponse
            {
                Id = deployment.Id,
                Status = deployment.Status,
                CreatedAtUtc = deployment.CreatedAt
            });
    }

    private static bool IsSupportedForImport(string moduleName)    {
        var normalized = moduleName.ToLowerInvariant().Trim();
        return normalized is
            "resource-group"
            or "storage-account"
            or "keyvault"
            or "virtual-network"
            or "network-security-group"
            or "network-security-rule"
            or "public-ip"
            or "local-network-gateway"
            or "virtual-network-gateway"
            or "virtual-network-peering"
            or "bastion-host"
            or "subnet";
    }

    /// <summary>
    /// Returns Terraform resource addresses that must be replaced (destroyed then re-created)
    /// on retry because Azure does not support in-place updates to those extension types.
    /// </summary>
    private static IReadOnlyList<string> BuildReplaceResources(string moduleName, JsonElement inputs)
    {
        var result = new List<string>();
        var normalized = moduleName.ToLowerInvariant().Trim();

        if (normalized == "windows-server-marketplace")
        {
            // JsonADDomainExtension cannot be updated in-place. If domain_name is set,
            // force a replace so the extension is removed and re-joined with the correct domain.
            if (inputs.TryGetProperty("domain_name", out var domainEl)
                && domainEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(domainEl.GetString()))
            {
                result.Add("azurerm_virtual_machine_extension.domain_join[0]");
            }

            // CustomScriptExtension also cannot be updated in-place once it has run.
            var hasPackages = inputs.TryGetProperty("chocolatey_packages", out var pkgsEl)
                && pkgsEl.ValueKind == JsonValueKind.Array && pkgsEl.GetArrayLength() > 0;
            var hasScriptUri = inputs.TryGetProperty("post_install_script_uri", out var uriEl)
                && uriEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(uriEl.GetString());
            if (hasPackages || hasScriptUri)
            {
                result.Add("azurerm_virtual_machine_extension.post_install[0]");
            }
        }

        return result;
    }

    /// <summary>
    /// Injects __replace_resources into the inputs JSON so the worker can pass
    /// -replace=&lt;address&gt; flags to terraform apply.
    /// </summary>
    private static JsonElement InjectReplaceResources(JsonElement inputs, IReadOnlyList<string> addresses)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (inputs.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in inputs.EnumerateObject())
                merged[property.Name] = property.Value.Clone();
        }
        merged["__replace_resources"] = JsonSerializer.SerializeToElement(addresses);
        return JsonSerializer.SerializeToElement(merged);
    }

    private static Dictionary<string, JsonElement> InjectImportMeta(JsonElement inputs, string resourceId)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (inputs.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in inputs.EnumerateObject())
            {
                merged[property.Name] = property.Value.Clone();
            }
        }

        merged["__operation"] = JsonSerializer.SerializeToElement("import");
        merged["__resource_id"] = JsonSerializer.SerializeToElement(resourceId);
        return merged;
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DeploymentDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeploymentDetailsResponse>> GetDeploymentById(Guid id, CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();

        var deployment = await dbContext.Deployments
            .Include(x => x.Module)
            .Include(x => x.Input)
            .Include(x => x.Output)
            .SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, cancellationToken);

        if (deployment is null || deployment.Module is null || deployment.Input is null)
        {
            return NotFound(new { message = "Deployment not found." });
        }

        return Ok(new DeploymentDetailsResponse
        {
            Id = deployment.Id,
            ModuleId = deployment.ModuleId,
            ModuleName = deployment.Module.Name,
            ModuleVersion = deployment.Module.Version,
            Status = deployment.Status,
            ErrorMessage = deployment.ErrorMessage,
            RetryCount = deployment.RetryCount,
            TerraformStatePath = deployment.TerraformStatePath,
            CreatedAtUtc = deployment.CreatedAt,
            UpdatedAtUtc = deployment.UpdatedAt,
            CompletedAtUtc = deployment.CompletedAt,
            Inputs = JsonHelpers.ParseJsonOrEmpty(deployment.Input.Inputs),
            Outputs = JsonHelpers.ParseNullableJson(deployment.Output?.Outputs)
        });
    }

    [HttpGet("{id:guid}/logs")]
    [ProducesResponseType(typeof(IReadOnlyList<DeploymentLogResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<DeploymentLogResponse>>> GetDeploymentLogs(
        Guid id,
        [FromQuery] long? sinceId,
        CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();

        var deploymentExists = await dbContext.Deployments
            .AnyAsync(x => x.Id == id && x.CustomerId == customerId, cancellationToken);

        if (!deploymentExists)
        {
            return NotFound(new { message = "Deployment not found." });
        }

        var logsQuery = dbContext.DeploymentLogs
            .Where(x => x.DeploymentId == id);

        if (sinceId.HasValue)
        {
            logsQuery = logsQuery.Where(x => x.Id > sinceId.Value);
        }

        var logs = await logsQuery
            .OrderBy(x => x.Id)
            .Take(200)
            .Select(x => new DeploymentLogResponse
            {
                Id = x.Id,
                TimestampUtc = x.Timestamp,
                Level = x.Level,
                Message = x.Message,
                Context = JsonHelpers.ParseNullableJson(x.Context)
            })
            .ToListAsync(cancellationToken);

        return Ok(logs);
    }

    [HttpPost("check-storage-name")]
    [ProducesResponseType(typeof(StorageNameAvailabilityCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StorageNameAvailabilityCheckResponse>> CheckStorageAccountName(
        [FromBody] CheckStorageAccountNameRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Storage account name is required." });
        }

        if (storageAccountNameAvailabilityService is null)
        {
            return BadRequest(new { message = "Storage account availability check is not configured." });
        }

        var customerId = User.GetRequiredCustomerId();
        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

        if (customer is null)
        {
            return BadRequest(new { message = "Customer is not active or does not exist." });
        }

        if (string.IsNullOrWhiteSpace(customer.TenantId) 
            || string.IsNullOrWhiteSpace(customer.SubscriptionId))
        {
            return BadRequest(new
            {
                message = "Customer subscription/tenant configuration is incomplete."
            });
        }

        var result = await storageAccountNameAvailabilityService.CheckAsync(customer, request.Name, cancellationToken);
        
        return Ok(new StorageNameAvailabilityCheckResponse
        {
            IsAvailable = result.IsAvailable,
            Message = result.Message,
            NameChecked = request.Name
        });
    }

    private static JsonElement EnsureModuleDefaultInputs(string moduleName, JsonElement inputs, CustomerEntity customer)
    {
        if (inputs.ValueKind != JsonValueKind.Object)
        {
            return inputs;
        }

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in inputs.EnumerateObject())
        {
            merged[property.Name] = property.Value.Clone();
        }

        if (IsKeyVaultModuleName(moduleName))
        {
            var normalizedTenantId = customer.TenantId?.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedTenantId))
            {
                // Force module tenant_id to customer metadata to avoid mismatched tenant context.
                merged["tenant_id"] = JsonSerializer.SerializeToElement(normalizedTenantId);
            }
        }

        if (IsVirtualNetworkModuleName(moduleName)
            && merged.TryGetValue("subnets", out var subnets)
            && subnets.ValueKind == JsonValueKind.Array
            && subnets.GetArrayLength() > 0)
        {
            var hasSubnetCount = merged.TryGetValue("subnet_count", out var subnetCount);
            var subnetCountBlank = !hasSubnetCount
                || subnetCount.ValueKind == JsonValueKind.Null
                || (subnetCount.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(subnetCount.GetString()));

            if (subnetCountBlank)
            {
                // Legacy virtual-network versions still validate subnet_count even when explicit subnets are provided.
                merged["subnet_count"] = JsonSerializer.SerializeToElement("1");
            }
        }

        return JsonSerializer.SerializeToElement(merged);
    }

    private static bool IsKeyVaultModuleName(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return false;
        }

        var normalized = Regex.Replace(moduleName, "[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase);
        return normalized.Contains("keyvault", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVirtualNetworkModuleName(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return false;
        }

        var normalized = Regex.Replace(moduleName, "[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase);
        return normalized.Contains("virtualnetwork", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ValidateInputsAgainstSchema(string? schemaJson, JsonElement inputs)
    {
        if (string.IsNullOrWhiteSpace(schemaJson) || inputs.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        using var schemaDocument = JsonDocument.Parse(schemaJson);
        var root = schemaDocument.RootElement;

        if (root.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var requiredField in required.EnumerateArray())
            {
                var fieldName = requiredField.GetString();
                if (string.IsNullOrWhiteSpace(fieldName))
                {
                    continue;
                }

                if (!inputs.TryGetProperty(fieldName, out var value)
                    || value.ValueKind == JsonValueKind.Null
                    || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
                {
                    return $"{fieldName} is required.";
                }
            }
        }

        if (!root.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var schemaProperty in properties.EnumerateObject())
        {
            if (!inputs.TryGetProperty(schemaProperty.Name, out var inputValue))
            {
                continue;
            }

            var propertySchema = schemaProperty.Value;

            if (propertySchema.TryGetProperty("type", out var typeElement))
            {
                var propertyType = typeElement.GetString();
                if (string.Equals(propertyType, "object", StringComparison.OrdinalIgnoreCase)
                    && inputValue.ValueKind != JsonValueKind.Object)
                {
                    return $"{schemaProperty.Name} must be a JSON object.";
                }
            }

            if (inputValue.ValueKind == JsonValueKind.String
                && propertySchema.TryGetProperty("pattern", out var patternElement))
            {
                var pattern = patternElement.GetString();
                var rawValue = inputValue.GetString() ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(pattern) && !Regex.IsMatch(rawValue, pattern))
                {
                    if (propertySchema.TryGetProperty("validationMessage", out var validationMessage)
                        && !string.IsNullOrWhiteSpace(validationMessage.GetString()))
                    {
                        return validationMessage.GetString();
                    }

                    if (propertySchema.TryGetProperty("description", out var description)
                        && !string.IsNullOrWhiteSpace(description.GetString()))
                    {
                        return description.GetString();
                    }

                    return $"{schemaProperty.Name} is invalid.";
                }
            }
        }

        return null;
    }

    [HttpPost("{id:guid}/retry")]
    [ProducesResponseType(typeof(DeploymentCreatedResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeploymentCreatedResponse>> RetryDeployment(
        Guid id,
        [FromBody] RetryDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();
        var userId = User.GetRequiredUserId();

        var failed = await dbContext.Deployments
            .Include(x => x.Input)
            .Include(x => x.Module)
            .SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, cancellationToken);

        if (failed is null || failed.Input is null || failed.Module is null)
            return NotFound(new { message = "Deployment not found." });

        // Allow retry once the deployment has failed at least once, even if the worker
        // has already re-queued it for an automatic retry attempt (Status == QUEUED, RetryCount > 0).
        // This lets the user submit corrected inputs immediately without waiting for all
        // auto-retry attempts to be exhausted.
        var canRetry = string.Equals(failed.Status, "FAILED", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(failed.Status, "QUEUED", StringComparison.OrdinalIgnoreCase) && failed.RetryCount > 0);

        if (!canRetry)
            return BadRequest(new { message = "Only failed deployments (or deployments being auto-retried after a failure) can be retried." });

        if (string.IsNullOrWhiteSpace(failed.TerraformStatePath))
            return BadRequest(new { message = "Deployment has no Terraform state path — cannot retry." });

        // Validate the corrected inputs against the module schema.
        var effectiveInputs = EnsureModuleDefaultInputs(failed.Module.Name, request.Inputs, 
            await dbContext.Customers.SingleAsync(x => x.Id == customerId, cancellationToken));

        var schemaJson = failed.Module.Schema;
        if (allowedRegionCatalogService is not null)
        {
            var allowedRegionCodes = await allowedRegionCatalogService.GetAllowedRegionCodesAsync(cancellationToken);
            schemaJson = allowedRegionCatalogService.ApplyAllowedRegionsToSchemaJson(failed.Module.Schema, allowedRegionCodes) ?? failed.Module.Schema;
        }

        var validationError = ValidateInputsAgainstSchema(schemaJson, effectiveInputs);
        if (validationError is not null)
            return BadRequest(new { message = validationError });

        var preflight = await preflightService.CheckAsync(
            await dbContext.Customers.SingleAsync(x => x.Id == customerId, cancellationToken),
            cancellationToken);

        if (!preflight.CanProceed)
        {
            return BadRequest(new
            {
                message = "Retry blocked by credential preflight checks.",
                issues = preflight.Issues,
                warnings = preflight.Warnings
            });
        }

        var now = DateTime.UtcNow;

        // Reuse the original state path so Terraform targets the same state file
        // and performs an incremental apply rather than provisioning from scratch.
        //
        // Some VM extensions cannot be updated in-place by Azure (e.g. JsonADDomainExtension).
        // Detect these in the corrected inputs and inject __replace_resources so the worker
        // passes -replace=<address> to terraform apply, forcing a destroy-then-recreate of
        // just the extension — the VM itself is untouched.
        var replaceResources = BuildReplaceResources(failed.Module.Name, effectiveInputs);
        var inputsToStore = replaceResources.Count > 0
            ? InjectReplaceResources(effectiveInputs, replaceResources)
            : effectiveInputs;

        var retryDeployment = new DeploymentEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ModuleId = failed.ModuleId,
            RequestedBy = userId,
            Status = "QUEUED",
            RetryCount = failed.RetryCount + 1,
            CreatedAt = now,
            UpdatedAt = now,
            TerraformStatePath = failed.TerraformStatePath
        };

        dbContext.Deployments.Add(retryDeployment);
        dbContext.DeploymentInputs.Add(new DeploymentInputEntity
        {
            Id = Guid.NewGuid(),
            DeploymentId = retryDeployment.Id,
            Inputs = JsonSerializer.Serialize(inputsToStore),
            CreatedAt = now
        });
        dbContext.DeploymentLogs.Add(new DeploymentLogEntity
        {
            DeploymentId = retryDeployment.Id,
            Timestamp = now,
            Level = "INFO",
            Message = "Retry deployment queued.",
            Context = JsonSerializer.Serialize(new
            {
                originalDeploymentId = failed.Id,
                module = failed.Module.Name,
                moduleVersion = failed.Module.Version,
                retryCount = retryDeployment.RetryCount
            })
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetDeploymentById),
            new { id = retryDeployment.Id },
            new DeploymentCreatedResponse
            {
                Id = retryDeployment.Id,
                Status = retryDeployment.Status,
                CreatedAtUtc = retryDeployment.CreatedAt
            });
    }

    [HttpPost("{id:guid}/destroy")]
    [ProducesResponseType(typeof(DeploymentCreatedResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeploymentCreatedResponse>> DestroyDeployment(Guid id, CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();
        var userId = User.GetRequiredUserId();

        var target = await dbContext.Deployments
            .Include(x => x.Input)
            .Include(x => x.Module)
            .SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, cancellationToken);

        if (target is null || target.Input is null || target.Module is null)
        {
            return NotFound(new { message = "Deployment not found." });
        }

        if (!string.Equals(target.Status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Only succeeded deployments can be destroyed." });
        }

        if (string.IsNullOrWhiteSpace(target.TerraformStatePath))
        {
            return BadRequest(new { message = "Deployment has no terraform state path for destroy operation." });
        }

        using var sourceInput = JsonDocument.Parse(target.Input.Inputs);
        var sourceJson = sourceInput.RootElement;
        var payload = sourceJson.ValueKind == JsonValueKind.Object
            ? sourceJson.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        payload["__operation"] = JsonSerializer.SerializeToElement("destroy");
        payload["__targetDeploymentId"] = JsonSerializer.SerializeToElement(target.Id);

        var now = DateTime.UtcNow;
        var destroyDeployment = new DeploymentEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ModuleId = target.ModuleId,
            RequestedBy = userId,
            Status = "QUEUED",
            RetryCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            TerraformStatePath = target.TerraformStatePath
        };

        dbContext.Deployments.Add(destroyDeployment);
        dbContext.DeploymentInputs.Add(new DeploymentInputEntity
        {
            Id = Guid.NewGuid(),
            DeploymentId = destroyDeployment.Id,
            Inputs = JsonSerializer.Serialize(payload),
            CreatedAt = now
        });
        dbContext.DeploymentLogs.Add(new DeploymentLogEntity
        {
            DeploymentId = destroyDeployment.Id,
            Timestamp = now,
            Level = "INFO",
            Message = "Destroy deployment queued.",
            Context = JsonSerializer.Serialize(new
            {
                targetDeploymentId = target.Id,
                module = target.Module.Name,
                moduleVersion = target.Module.Version,
                operation = "destroy"
            })
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetDeploymentById),
            new { id = destroyDeployment.Id },
            new DeploymentCreatedResponse
            {
                Id = destroyDeployment.Id,
                Status = destroyDeployment.Status,
                CreatedAtUtc = destroyDeployment.CreatedAt
            });
    }

    [HttpPost("{id:guid}/rebuild")]
    [ProducesResponseType(typeof(RebuildDeploymentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RebuildDeploymentResponse>> RebuildDeployment(Guid id, CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();
        var userId = User.GetRequiredUserId();

        var target = await dbContext.Deployments
            .Include(x => x.Input)
            .Include(x => x.Module)
            .SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, cancellationToken);

        if (target is null || target.Input is null || target.Module is null)
        {
            return NotFound(new { message = "Deployment not found." });
        }

        if (!string.Equals(target.Status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Only succeeded deployments can be rebuilt." });
        }

        if (string.IsNullOrWhiteSpace(target.TerraformStatePath))
        {
            return BadRequest(new { message = "Deployment has no terraform state path for rebuild operation." });
        }

        using var sourceInput = JsonDocument.Parse(target.Input.Inputs);
        var sourceJson = sourceInput.RootElement;
        var sourcePayload = sourceJson.ValueKind == JsonValueKind.Object
            ? sourceJson.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        // Remove internal metadata keys before re-queueing a create deployment.
        var cleanedPayload = sourcePayload
            .Where(x => !x.Key.StartsWith("__", StringComparison.Ordinal))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

        var destroyPayload = new Dictionary<string, JsonElement>(cleanedPayload, StringComparer.Ordinal)
        {
            ["__operation"] = JsonSerializer.SerializeToElement("destroy"),
            ["__targetDeploymentId"] = JsonSerializer.SerializeToElement(target.Id)
        };

        var now = DateTime.UtcNow;
        var destroyDeployment = new DeploymentEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ModuleId = target.ModuleId,
            RequestedBy = userId,
            Status = "QUEUED",
            RetryCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            TerraformStatePath = target.TerraformStatePath
        };

        // Ensure the redeploy has a later CreatedAt than destroy so worker processing
        // order is deterministic (worker dequeues QUEUED deployments by CreatedAt ASC).
        var redeployCreatedAt = now.AddMilliseconds(1);
        var redeployDeployment = new DeploymentEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ModuleId = target.ModuleId,
            RequestedBy = userId,
            Status = "QUEUED",
            RetryCount = 0,
            CreatedAt = redeployCreatedAt,
            UpdatedAt = redeployCreatedAt,
            TerraformStatePath = target.TerraformStatePath
        };

        dbContext.Deployments.Add(destroyDeployment);
        dbContext.Deployments.Add(redeployDeployment);

        dbContext.DeploymentInputs.Add(new DeploymentInputEntity
        {
            Id = Guid.NewGuid(),
            DeploymentId = destroyDeployment.Id,
            Inputs = JsonSerializer.Serialize(destroyPayload),
            CreatedAt = now
        });
        dbContext.DeploymentInputs.Add(new DeploymentInputEntity
        {
            Id = Guid.NewGuid(),
            DeploymentId = redeployDeployment.Id,
            Inputs = JsonSerializer.Serialize(cleanedPayload),
            CreatedAt = redeployCreatedAt
        });

        dbContext.DeploymentLogs.Add(new DeploymentLogEntity
        {
            DeploymentId = destroyDeployment.Id,
            Timestamp = now,
            Level = "INFO",
            Message = "Rebuild queued: destroy step.",
            Context = JsonSerializer.Serialize(new
            {
                sourceDeploymentId = target.Id,
                rebuildStep = "destroy",
                nextDeploymentId = redeployDeployment.Id,
                module = target.Module.Name,
                moduleVersion = target.Module.Version
            })
        });
        dbContext.DeploymentLogs.Add(new DeploymentLogEntity
        {
            DeploymentId = redeployDeployment.Id,
            Timestamp = redeployCreatedAt,
            Level = "INFO",
            Message = "Rebuild queued: redeploy step.",
            Context = JsonSerializer.Serialize(new
            {
                sourceDeploymentId = target.Id,
                rebuildStep = "redeploy",
                dependsOnDeploymentId = destroyDeployment.Id,
                module = target.Module.Name,
                moduleVersion = target.Module.Version
            })
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetDeploymentById),
            new { id = redeployDeployment.Id },
            new RebuildDeploymentResponse
            {
                DestroyDeploymentId = destroyDeployment.Id,
                RedeployDeploymentId = redeployDeployment.Id,
                Status = redeployDeployment.Status,
                CreatedAtUtc = redeployDeployment.CreatedAt
            });
    }

    [HttpPost("rebuild-all")]
    [ProducesResponseType(typeof(RebuildAllResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RebuildAllResponse>> RebuildAllDeployments(CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();
        var userId = User.GetRequiredUserId();

        var deployments = await dbContext.Deployments
            .Include(x => x.Input)
            .Include(x => x.Module)
            .Where(x => x.CustomerId == customerId)
            .Where(x => x.Input != null && x.Module != null)
            .Where(x => !string.IsNullOrWhiteSpace(x.TerraformStatePath))
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.UpdatedAt)
            .ToListAsync(cancellationToken);

        var sources = deployments
            .GroupBy(x => NormalizeStatePath(x.TerraformStatePath!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(x => string.Equals(x.Status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .ToList();

        if (sources.Count == 0)
        {
            return BadRequest(new { message = "No succeeded deployments were found to rebuild." });
        }

        var batchId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var queueIndex = 0;

        // Destroy in reverse build order.
        foreach (var source in sources.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id))
        {
            var createdAt = now.AddMilliseconds(queueIndex * 2);
            queueIndex += 1;

            using var sourceInput = JsonDocument.Parse(source.Input!.Inputs);
            var sourceJson = sourceInput.RootElement;
            var sourcePayload = sourceJson.ValueKind == JsonValueKind.Object
                ? sourceJson.EnumerateObject().Where(x => !x.Name.StartsWith("__", StringComparison.Ordinal)).ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            var destroyPayload = new Dictionary<string, JsonElement>(sourcePayload, StringComparer.Ordinal)
            {
                ["__operation"] = JsonSerializer.SerializeToElement("destroy"),
                ["__targetDeploymentId"] = JsonSerializer.SerializeToElement(source.Id),
                ["__rebuild_batch_id"] = JsonSerializer.SerializeToElement(batchId),
                ["__rebuild_phase"] = JsonSerializer.SerializeToElement("destroy")
            };

            var destroyDeployment = new DeploymentEntity
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                ModuleId = source.ModuleId,
                RequestedBy = userId,
                Status = "QUEUED",
                RetryCount = 0,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                TerraformStatePath = source.TerraformStatePath
            };

            dbContext.Deployments.Add(destroyDeployment);
            dbContext.DeploymentInputs.Add(new DeploymentInputEntity
            {
                Id = Guid.NewGuid(),
                DeploymentId = destroyDeployment.Id,
                Inputs = JsonSerializer.Serialize(destroyPayload),
                CreatedAt = createdAt
            });
            dbContext.DeploymentLogs.Add(new DeploymentLogEntity
            {
                DeploymentId = destroyDeployment.Id,
                Timestamp = createdAt,
                Level = "INFO",
                Message = "Rebuild-all queued: destroy step.",
                Context = JsonSerializer.Serialize(new
                {
                    batchId,
                    sourceDeploymentId = source.Id,
                    rebuildPhase = "destroy",
                    module = source.Module.Name,
                    moduleVersion = source.Module.Version
                })
            });
        }

        // Redeploy in original build order.
        foreach (var source in sources.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id))
        {
            var createdAt = now.AddMilliseconds((sources.Count * 2) + (queueIndex * 2));
            queueIndex += 1;

            using var sourceInput = JsonDocument.Parse(source.Input!.Inputs);
            var sourceJson = sourceInput.RootElement;
            var cleanPayload = sourceJson.ValueKind == JsonValueKind.Object
                ? sourceJson.EnumerateObject().Where(x => !x.Name.StartsWith("__", StringComparison.Ordinal)).ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal)
                : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            var redeployDeployment = new DeploymentEntity
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                ModuleId = source.ModuleId,
                RequestedBy = userId,
                Status = "QUEUED",
                RetryCount = 0,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                TerraformStatePath = source.TerraformStatePath
            };

            dbContext.Deployments.Add(redeployDeployment);
            dbContext.DeploymentInputs.Add(new DeploymentInputEntity
            {
                Id = Guid.NewGuid(),
                DeploymentId = redeployDeployment.Id,
                Inputs = JsonSerializer.Serialize(cleanPayload),
                CreatedAt = createdAt
            });
            dbContext.DeploymentLogs.Add(new DeploymentLogEntity
            {
                DeploymentId = redeployDeployment.Id,
                Timestamp = createdAt,
                Level = "INFO",
                Message = "Rebuild-all queued: redeploy step.",
                Context = JsonSerializer.Serialize(new
                {
                    batchId,
                    sourceDeploymentId = source.Id,
                    rebuildPhase = "redeploy",
                    module = source.Module.Name,
                    moduleVersion = source.Module.Version
                })
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return Accepted(new RebuildAllResponse
        {
            BatchId = batchId,
            DeploymentCount = sources.Count,
            DestroyCount = sources.Count,
            RedeployCount = sources.Count
        });
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteFailedDeployment(Guid id, CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();

        var target = await dbContext.Deployments
            .SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, cancellationToken);

        if (target is null)
        {
            return NotFound(new { message = "Deployment not found." });
        }

        if (!string.Equals(target.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Only failed deployments can be deleted." });
        }

        dbContext.Deployments.Remove(target);
        await dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private static ManagedResourceResponse ToManagedResourceResponse(DeploymentEntity deployment)
    {
        var inputs = JsonHelpers.ParseNullableJson(deployment.Input?.Inputs);
        var outputs = JsonHelpers.ParseNullableJson(deployment.Output?.Outputs);

        var resourceName = GetJsonString(outputs, "name")
            ?? GetJsonString(inputs, "name");

        var resourceLocation = GetJsonString(outputs, "location")
            ?? GetJsonString(inputs, "location");

        var resourceId = GetJsonString(outputs, "id")
            ?? GetJsonString(outputs, "vnet_id");

        return new ManagedResourceResponse
        {
            DeploymentId = deployment.Id,
            ModuleId = deployment.ModuleId,
            ModuleName = deployment.Module?.Name ?? string.Empty,
            ModuleVersion = deployment.Module?.Version ?? string.Empty,
            Status = deployment.Status,
            ResourceName = resourceName ?? deployment.Module?.Name ?? deployment.Id.ToString("N"),
            ResourceLocation = resourceLocation ?? string.Empty,
            ResourceId = resourceId ?? string.Empty,
            TerraformStatePath = deployment.TerraformStatePath,
            CreatedAtUtc = deployment.CreatedAt,
            UpdatedAtUtc = deployment.UpdatedAt,
            CompletedAtUtc = deployment.CompletedAt
        };
    }

    private static string NormalizeStatePath(string statePath)
        => statePath.Replace('\\', '/').Trim();

    private static string BuildDeterministicStatePath(Guid customerId, Guid moduleId, string moduleName, JsonElement inputs)
    {
        var identityToken = BuildStateIdentityToken(moduleName, inputs);

        // Hash only the identity-stable fields so that changing configuration (e.g. domain_name,
        // VM size, software packages) reuses the same state file rather than creating a new one.
        // Hashing all inputs caused a fresh state path — and a "resource already exists" error
        // from Terraform — whenever any input was corrected between deployments.
        var name = inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty("name", out var n)
            ? n.GetString() ?? string.Empty : string.Empty;
        var resourceGroup = inputs.ValueKind == JsonValueKind.Object
            && (inputs.TryGetProperty("resource_group_name", out var rg) || inputs.TryGetProperty("resourceGroupName", out rg))
            ? rg.GetString() ?? string.Empty : string.Empty;

        var stableIdentityParts = GetStableIdentityParts(moduleName, inputs);
        var stableKey = stableIdentityParts.Count == 0
            ? $"{moduleName}:{resourceGroup}:{name}"
            : string.Join(':', stableIdentityParts);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(stableKey));
        var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant()[..16];

        return $"tfstate/customers/{customerId}/{moduleId}/{identityToken}-{hashHex}.tfstate";
    }

    private static string BuildStateIdentityToken(string moduleName, JsonElement inputs)
    {
        var moduleToken = SlugifyToken(moduleName, "module");

        if (inputs.ValueKind != JsonValueKind.Object)
        {
            return moduleToken;
        }

        var stableIdentityParts = GetStableIdentityParts(moduleName, inputs);

        if (stableIdentityParts.Count > 1)
        {
            return SlugifyToken(string.Join('-', stableIdentityParts.Skip(1)), moduleToken);
        }

        var name = TryGetStateString(inputs, "name");
        var resourceGroup = TryGetStateString(inputs, "resource_group_name") ?? TryGetStateString(inputs, "resourceGroupName");

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(resourceGroup))
        {
            return SlugifyToken($"{moduleToken}-{resourceGroup}-{name}", moduleToken);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return SlugifyToken($"{moduleToken}-{name}", moduleToken);
        }

        return moduleToken;
    }

    private static IReadOnlyList<string> GetStableIdentityParts(string moduleName, JsonElement inputs)
    {
        var parts = new List<string> { moduleName.Trim().ToLowerInvariant() };

        AddStablePart(parts, TryGetStateString(inputs, "resource_group_name") ?? TryGetStateString(inputs, "resourceGroupName"));
        AddStablePart(parts, TryGetStateString(inputs, "virtual_network_name"));
        AddStablePart(parts, TryGetStateString(inputs, "network_security_group_name"));
        AddStablePart(parts, TryGetStateString(inputs, "name"));

        return parts;
    }

    private static void AddStablePart(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value.Trim());
        }
    }

    private async Task<HashSet<string>> GetManagedResourceIdsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var deployments = await dbContext.Deployments
            .Include(x => x.Module)
            .Include(x => x.Input)
            .Include(x => x.Output)
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.UpdatedAt)
            .ToListAsync(cancellationToken);

        return deployments
            .Where(x => !string.IsNullOrWhiteSpace(x.TerraformStatePath))
            .GroupBy(x => NormalizeStatePath(x.TerraformStatePath!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(ToManagedResourceResponse)
            .Select(x => x.ResourceId)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? TryGetStateString(JsonElement inputs, string key)
    {
        if (!inputs.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string SlugifyToken(string rawValue, string fallback)
    {
        var normalized = Regex.Replace(rawValue.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = fallback;
        }

        return normalized.Length > 60 ? normalized[..60] : normalized;
    }

    private static string CanonicalizeJson(JsonElement element)
    {
        var builder = new StringBuilder();
        AppendCanonicalJson(element, builder);
        return builder.ToString();
    }

    private static void AppendCanonicalJson(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Name));
                    builder.Append(':');
                    AppendCanonicalJson(property.Value, builder);
                }

                builder.Append('}');
                break;
            }
            case JsonValueKind.Array:
            {
                builder.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    AppendCanonicalJson(item, builder);
                }

                builder.Append(']');
                break;
            }
            default:
                builder.Append(element.GetRawText());
                break;
        }
    }

    private static string? GetJsonString(JsonElement? element, string propertyName)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.Value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Object && property.TryGetProperty("value", out var valueElement))
        {
            if (valueElement.ValueKind == JsonValueKind.String)
            {
                return valueElement.GetString();
            }

            return valueElement.ToString();
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return property.ToString();
    }

    private static bool TryParseResourceIdName(string? resourceId, string expectedTypeSegment, out string name)
    {
        name = string.Empty;

        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return false;
        }

        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!string.Equals(parts[i], expectedTypeSegment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            name = parts[i + 1];
            return !string.IsNullOrWhiteSpace(name);
        }

        return false;
    }

    private static bool TryParseResourceIdResourceGroup(string? resourceId, out string resourceGroupName)
    {
        resourceGroupName = string.Empty;

        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return false;
        }

        var parts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!string.Equals(parts[i], "resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            resourceGroupName = parts[i + 1];
            return !string.IsNullOrWhiteSpace(resourceGroupName);
        }

        return false;
    }

    private static string EscapeTerraformAddressKey(string key)
    {
        return key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}