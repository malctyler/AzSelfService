using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using AzSelfService.API.Data.Entities;

namespace AzSelfService.API.Services;

public sealed record VirtualNetworkSubnetSummary(
    string Name,
    string SubnetId,
    string AddressPrefix,
    string? NetworkSecurityGroupId = null,
    IReadOnlyList<string>? ServiceEndpoints = null);

public sealed class VirtualNetworkInfo
{
    public bool Found { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string AddressSpace { get; init; } = string.Empty;
    public IReadOnlyList<VirtualNetworkSubnetSummary> Subnets { get; init; } = Array.Empty<VirtualNetworkSubnetSummary>();
    public Dictionary<string, string> Tags { get; init; } = new();
    public string? ErrorMessage { get; init; }

    public static VirtualNetworkInfo NotFound(string message) =>
        new() { Found = false, ErrorMessage = message };
}

public sealed class ArmLookupResult
{
    public bool Found { get; init; }
    public string? ResourceId { get; init; }
    public string? Location { get; init; }
    public Dictionary<string, string> ExistingTags { get; init; } = new();
    public string? ErrorMessage { get; init; }

    public static ArmLookupResult NotFound(string message) =>
        new() { Found = false, ErrorMessage = message };

    public static ArmLookupResult Success(string resourceId, string location, Dictionary<string, string> tags) =>
        new() { Found = true, ResourceId = resourceId, Location = location, ExistingTags = tags };
}

public sealed class ResourceGroupLookupService(
    IServiceProvider serviceProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<ResourceGroupLookupService> logger)
{
    private const string ResourceGroupApiVersion = "2021-04-01";
    private const string StorageAccountApiVersion = "2023-01-01";

    // Tags written by Terraform modules via merge() — strip so they don't end up in var.tags
    private static readonly HashSet<string> ManagedTagKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ManagedBy", "CreatedAt", "Environment"
    };

    public Task<ArmLookupResult> LookupAsync(
        CustomerEntity customer,
        string name,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(ArmLookupResult.NotFound("Resource group name is required."));

        var url = $"https://management.azure.com/subscriptions/{customer.SubscriptionId}/resourceGroups/{Uri.EscapeDataString(name)}?api-version={ResourceGroupApiVersion}";
        return LookupArmResourceAsync(customer, url, $"resource group '{name}'", cancellationToken);
    }

    public Task<ArmLookupResult> LookupStorageAccountAsync(
        CustomerEntity customer,
        string resourceGroupName,
        string storageAccountName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storageAccountName))
            return Task.FromResult(ArmLookupResult.NotFound("Storage account name is required."));
        if (string.IsNullOrWhiteSpace(resourceGroupName))
            return Task.FromResult(ArmLookupResult.NotFound("Resource group name is required."));

        var url = $"https://management.azure.com/subscriptions/{customer.SubscriptionId}" +
                  $"/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}" +
                  $"/providers/Microsoft.Storage/storageAccounts/{Uri.EscapeDataString(storageAccountName)}" +
                  $"?api-version={StorageAccountApiVersion}";
        return LookupArmResourceAsync(customer, url, $"storage account '{storageAccountName}'", cancellationToken);
    }

    public Task<ArmLookupResult> LookupKeyVaultAsync(
        CustomerEntity customer,
        string resourceGroupName,
        string keyVaultName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyVaultName))
            return Task.FromResult(ArmLookupResult.NotFound("Key vault name is required."));
        if (string.IsNullOrWhiteSpace(resourceGroupName))
            return Task.FromResult(ArmLookupResult.NotFound("Resource group name is required."));

        var url = $"https://management.azure.com/subscriptions/{customer.SubscriptionId}" +
                  $"/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}" +
                  $"/providers/Microsoft.KeyVault/vaults/{Uri.EscapeDataString(keyVaultName)}" +
                  $"?api-version=2023-07-01";
        return LookupArmResourceAsync(customer, url, $"key vault '{keyVaultName}'", cancellationToken);
    }

    public async Task<(IReadOnlyList<(string Name, string ResourceId, string Location, Dictionary<string, string> Tags)>? Accounts, string? ErrorMessage)>
        ListStorageAccountsAsync(
            CustomerEntity customer,
            string resourceGroupName,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceGroupName))
            return (null, "Resource group name is required.");

        if (string.IsNullOrWhiteSpace(customer.TenantId) || string.IsNullOrWhiteSpace(customer.SubscriptionId))
            return (null, "Customer tenant_id and subscription_id must be configured.");

        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef))
            return (null, "Customer service principal client secret reference is not configured.");

        var secretClient = serviceProvider.GetService<SecretClient>();
        if (secretClient is null)
            return (null, "Key Vault client is not configured on the API service.");

        try
        {
            var secretName = ResolveSecretReference(customer.SpClientSecretSecretRef);
            var secretResponse = await secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            var secret = secretResponse.Value;

            if (string.IsNullOrWhiteSpace(secret.Value))
                return (null, "Customer service principal secret is empty.");

            var appId = ResolveAppIdFromSecretMetadata(secret);
            if (string.IsNullOrWhiteSpace(appId))
                return (null, "Could not resolve appid from Key Vault secret metadata.");

            var credential = new ClientSecretCredential(customer.TenantId, appId, secret.Value);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                cancellationToken);

            var url = $"https://management.azure.com/subscriptions/{customer.SubscriptionId}" +
                      $"/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}" +
                      $"/providers/Microsoft.Storage/storageAccounts?api-version={StorageAccountApiVersion}";

            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("ARM list storage accounts failed for RG '{RG}'. Status={Status}, Body={Body}",
                    resourceGroupName, (int)response.StatusCode, errorBody);
                return (null, "Could not list storage accounts at this time.");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            var accounts = new List<(string Name, string ResourceId, string Location, Dictionary<string, string> Tags)>();

            if (root.TryGetProperty("value", out var valueEl) && valueEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in valueEl.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                    var location = item.TryGetProperty("location", out var locEl) ? locEl.GetString() ?? string.Empty : string.Empty;

                    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (item.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var tag in tagsEl.EnumerateObject())
                        {
                            if (!ManagedTagKeys.Contains(tag.Name) && tag.Value.ValueKind == JsonValueKind.String)
                                tags[tag.Name] = tag.Value.GetString() ?? string.Empty;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                        accounts.Add((name, id, location, tags));
                }
            }

            return (accounts, null);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return (null, "Customer service principal secret was not found in Key Vault.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "List storage accounts failed for RG '{RG}'.", resourceGroupName);
            return (null, "Could not list storage accounts at this time.");
        }
    }

    public async Task<(IReadOnlyList<(string Name, string ResourceId, string Location, Dictionary<string, string> Tags)>? Accounts, string? ErrorMessage)>
        ListKeyVaultsAsync(
            CustomerEntity customer,
            string resourceGroupName,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceGroupName))
            return (null, "Resource group name is required.");

        if (string.IsNullOrWhiteSpace(customer.TenantId) || string.IsNullOrWhiteSpace(customer.SubscriptionId))
            return (null, "Customer tenant_id and subscription_id must be configured.");

        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef))
            return (null, "Customer service principal client secret reference is not configured.");

        var secretClient = serviceProvider.GetService<SecretClient>();
        if (secretClient is null)
            return (null, "Key Vault client is not configured on the API service.");

        try
        {
            var secretName = ResolveSecretReference(customer.SpClientSecretSecretRef);
            var secretResponse = await secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            var secret = secretResponse.Value;

            if (string.IsNullOrWhiteSpace(secret.Value))
                return (null, "Customer service principal secret is empty.");

            var appId = ResolveAppIdFromSecretMetadata(secret);
            if (string.IsNullOrWhiteSpace(appId))
                return (null, "Could not resolve appid from Key Vault secret metadata.");

            var credential = new ClientSecretCredential(customer.TenantId, appId, secret.Value);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                cancellationToken);

            var url = $"https://management.azure.com/subscriptions/{customer.SubscriptionId}" +
                      $"/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}" +
                      $"/providers/Microsoft.KeyVault/vaults?api-version=2023-07-01";

            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("ARM list key vaults failed for RG '{RG}'. Status={Status}, Body={Body}",
                    resourceGroupName, (int)response.StatusCode, errorBody);
                return (null, "Could not list key vaults at this time.");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            var vaults = new List<(string Name, string ResourceId, string Location, Dictionary<string, string> Tags)>();

            if (root.TryGetProperty("value", out var valueEl) && valueEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in valueEl.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                    var location = item.TryGetProperty("location", out var locEl) ? locEl.GetString() ?? string.Empty : string.Empty;

                    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (item.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var tag in tagsEl.EnumerateObject())
                        {
                            if (!ManagedTagKeys.Contains(tag.Name) && tag.Value.ValueKind == JsonValueKind.String)
                                tags[tag.Name] = tag.Value.GetString() ?? string.Empty;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                        vaults.Add((name, id, location, tags));
                }
            }

            return (vaults, null);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return (null, "Customer service principal secret was not found in Key Vault.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "List key vaults failed for RG '{RG}'.", resourceGroupName);
            return (null, "Could not list key vaults at this time.");
        }
    }

    public async Task<VirtualNetworkInfo> LookupVirtualNetworkAsync(
        CustomerEntity customer,
        string resourceGroupName,
        string vnetName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vnetName))
            return VirtualNetworkInfo.NotFound("Virtual network name is required.");
        if (string.IsNullOrWhiteSpace(resourceGroupName))
            return VirtualNetworkInfo.NotFound("Resource group name is required.");

        if (string.IsNullOrWhiteSpace(customer.TenantId) || string.IsNullOrWhiteSpace(customer.SubscriptionId))
            return VirtualNetworkInfo.NotFound("Customer tenant_id and subscription_id must be configured.");
        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef))
            return VirtualNetworkInfo.NotFound("Customer service principal client secret reference is not configured.");

        var secretClient = serviceProvider.GetService<SecretClient>();
        if (secretClient is null)
            return VirtualNetworkInfo.NotFound("Key Vault client is not configured on the API service.");

        try
        {
            var secretName = ResolveSecretReference(customer.SpClientSecretSecretRef);
            var secretResponse = await secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            var secret = secretResponse.Value;

            if (string.IsNullOrWhiteSpace(secret.Value))
                return VirtualNetworkInfo.NotFound("Customer service principal secret is empty.");

            var appId = ResolveAppIdFromSecretMetadata(secret);
            if (string.IsNullOrWhiteSpace(appId))
                return VirtualNetworkInfo.NotFound("Could not resolve appid from Key Vault secret metadata.");

            var credential = new ClientSecretCredential(customer.TenantId, appId, secret.Value);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                cancellationToken);

            var url = $"https://management.azure.com/subscriptions/{customer.SubscriptionId}"
                    + $"/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}"
                    + $"/providers/Microsoft.Network/virtualNetworks/{Uri.EscapeDataString(vnetName)}"
                    + "?api-version=2023-05-01";

            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return VirtualNetworkInfo.NotFound($"Virtual network '{vnetName}' was not found.");

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("ARM VNet lookup failed for '{VNet}'. Status={Status}, Body={Body}",
                    vnetName, (int)response.StatusCode, errorBody);
                return VirtualNetworkInfo.NotFound($"Could not look up virtual network '{vnetName}' at this time.");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            return ParseVirtualNetworkDocument(document.RootElement);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return VirtualNetworkInfo.NotFound("Customer service principal secret was not found in Key Vault.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "VNet lookup failed for '{VNet}'.", vnetName);
            return VirtualNetworkInfo.NotFound($"Could not look up virtual network '{vnetName}' at this time.");
        }
    }

    public async Task<(IReadOnlyList<VirtualNetworkInfo>? VNets, string? ErrorMessage)> ListVirtualNetworksAsync(
        CustomerEntity customer,
        string resourceGroupName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resourceGroupName))
            return (null, "Resource group name is required.");
        if (string.IsNullOrWhiteSpace(customer.TenantId) || string.IsNullOrWhiteSpace(customer.SubscriptionId))
            return (null, "Customer tenant_id and subscription_id must be configured.");
        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef))
            return (null, "Customer service principal client secret reference is not configured.");

        var secretClient = serviceProvider.GetService<SecretClient>();
        if (secretClient is null)
            return (null, "Key Vault client is not configured on the API service.");

        try
        {
            var secretName = ResolveSecretReference(customer.SpClientSecretSecretRef);
            var secretResponse = await secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            var secret = secretResponse.Value;

            if (string.IsNullOrWhiteSpace(secret.Value))
                return (null, "Customer service principal secret is empty.");

            var appId = ResolveAppIdFromSecretMetadata(secret);
            if (string.IsNullOrWhiteSpace(appId))
                return (null, "Could not resolve appid from Key Vault secret metadata.");

            var credential = new ClientSecretCredential(customer.TenantId, appId, secret.Value);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                cancellationToken);

            var url = $"https://management.azure.com/subscriptions/{customer.SubscriptionId}"
                    + $"/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}"
                    + "/providers/Microsoft.Network/virtualNetworks?api-version=2023-05-01";

            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("ARM list VNets failed for RG '{RG}'. Status={Status}, Body={Body}",
                    resourceGroupName, (int)response.StatusCode, errorBody);
                return (null, "Could not list virtual networks at this time.");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            var vnets = new List<VirtualNetworkInfo>();
            if (root.TryGetProperty("value", out var valueEl) && valueEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in valueEl.EnumerateArray())
                {
                    var info = ParseVirtualNetworkDocument(item);
                    if (!string.IsNullOrWhiteSpace(info.Name))
                        vnets.Add(info);
                }
            }

            return (vnets, null);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return (null, "Customer service principal secret was not found in Key Vault.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "List VNets failed for RG '{RG}'.", resourceGroupName);
            return (null, "Could not list virtual networks at this time.");
        }
    }

    private VirtualNetworkInfo ParseVirtualNetworkDocument(JsonElement root)
    {
        var resourceId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
        var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
        var location = root.TryGetProperty("location", out var locEl) ? locEl.GetString() ?? string.Empty : string.Empty;

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in tagsEl.EnumerateObject())
            {
                if (!ManagedTagKeys.Contains(tag.Name) && tag.Value.ValueKind == JsonValueKind.String)
                    tags[tag.Name] = tag.Value.GetString() ?? string.Empty;
            }
        }

        var addressSpace = string.Empty;
        var subnets = new List<VirtualNetworkSubnetSummary>();

        if (root.TryGetProperty("properties", out var propsEl))
        {
            if (propsEl.TryGetProperty("addressSpace", out var asEl)
                && asEl.TryGetProperty("addressPrefixes", out var prefixesEl)
                && prefixesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var prefix in prefixesEl.EnumerateArray())
                {
                    var p = prefix.GetString();
                    if (!string.IsNullOrWhiteSpace(p)) { addressSpace = p; break; }
                }
            }

            if (propsEl.TryGetProperty("subnets", out var subnetsEl) && subnetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var subnetEl in subnetsEl.EnumerateArray())
                {
                    var sName = subnetEl.TryGetProperty("name", out var snEl) ? snEl.GetString() ?? string.Empty : string.Empty;
                    var sId = subnetEl.TryGetProperty("id", out var siEl) ? siEl.GetString() ?? string.Empty : string.Empty;
                    var sPrefix = string.Empty;
                    var sNsgId = (string?)null;
                    var sServiceEndpoints = new List<string>();

                    if (subnetEl.TryGetProperty("properties", out var spEl))
                    {
                        if (spEl.TryGetProperty("addressPrefixes", out var apsEl) && apsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var prefix in apsEl.EnumerateArray())
                            {
                                var p = prefix.GetString();
                                if (!string.IsNullOrWhiteSpace(p))
                                {
                                    sPrefix = p;
                                    break;
                                }
                            }
                        }
                        else if (spEl.TryGetProperty("addressPrefix", out var apEl))
                        {
                            sPrefix = apEl.GetString() ?? string.Empty;
                        }

                        if (spEl.TryGetProperty("serviceEndpoints", out var serviceEndpointsEl)
                            && serviceEndpointsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var endpointEl in serviceEndpointsEl.EnumerateArray())
                            {
                                if (endpointEl.ValueKind == JsonValueKind.Object
                                    && endpointEl.TryGetProperty("service", out var serviceEl)
                                    && serviceEl.ValueKind == JsonValueKind.String)
                                {
                                    var service = serviceEl.GetString();
                                    if (!string.IsNullOrWhiteSpace(service))
                                        sServiceEndpoints.Add(service);
                                }
                            }
                        }

                        // Extract NSG association if present
                        if (spEl.TryGetProperty("networkSecurityGroup", out var nsgEl) && nsgEl.ValueKind == JsonValueKind.Object)
                        {
                            sNsgId = nsgEl.TryGetProperty("id", out var nsgIdEl) ? nsgIdEl.GetString() : null;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(sName) && !string.IsNullOrWhiteSpace(sId))
                        subnets.Add(new VirtualNetworkSubnetSummary(sName, sId, sPrefix, sNsgId, sServiceEndpoints));
                }
            }
        }

        return new VirtualNetworkInfo
        {
            Found = true,
            Name = name,
            ResourceId = resourceId,
            Location = location,
            AddressSpace = addressSpace,
            Subnets = subnets,
            Tags = tags
        };
    }

    private async Task<ArmLookupResult> LookupArmResourceAsync(
        CustomerEntity customer,
        string url,
        string resourceDescription,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customer.TenantId) || string.IsNullOrWhiteSpace(customer.SubscriptionId))
            return ArmLookupResult.NotFound("Customer tenant_id and subscription_id must be configured.");

        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef))
            return ArmLookupResult.NotFound("Customer service principal client secret reference is not configured.");

        var secretClient = serviceProvider.GetService<SecretClient>();
        if (secretClient is null)
            return ArmLookupResult.NotFound("Key Vault client is not configured on the API service.");

        try
        {
            var secretName = ResolveSecretReference(customer.SpClientSecretSecretRef);
            var secretResponse = await secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            var secret = secretResponse.Value;

            if (string.IsNullOrWhiteSpace(secret.Value))
                return ArmLookupResult.NotFound("Customer service principal secret is empty.");

            var appId = ResolveAppIdFromSecretMetadata(secret);
            if (string.IsNullOrWhiteSpace(appId))
                return ArmLookupResult.NotFound("Could not resolve appid from Key Vault secret metadata.");

            var credential = new ClientSecretCredential(customer.TenantId, appId, secret.Value);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                cancellationToken);

            var httpClient = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return ArmLookupResult.NotFound($"Could not find {resourceDescription} in subscription '{customer.SubscriptionId}'.");

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning("ARM lookup failed for {Resource}. Status={Status}, Body={Body}",
                    resourceDescription, (int)response.StatusCode, errorBody);
                return ArmLookupResult.NotFound($"Could not look up {resourceDescription} at this time.");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            var resourceId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
            var location = root.TryGetProperty("location", out var locEl) ? locEl.GetString() ?? string.Empty : string.Empty;

            var userTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in tagsEl.EnumerateObject())
                {
                    if (!ManagedTagKeys.Contains(tag.Name) && tag.Value.ValueKind == JsonValueKind.String)
                        userTags[tag.Name] = tag.Value.GetString() ?? string.Empty;
                }
            }

            return ArmLookupResult.Success(resourceId, location, userTags);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return ArmLookupResult.NotFound("Customer service principal secret was not found in Key Vault.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ARM lookup failed for {Resource}.", resourceDescription);
            return ArmLookupResult.NotFound($"Could not look up {resourceDescription} at this time.");
        }
    }

    private static string ResolveSecretReference(string reference)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase))
                return segments[1];

            throw new InvalidOperationException($"Invalid Key Vault secret URI reference '{reference}'.");
        }

        return reference.Trim();
    }

    private static string? ResolveAppIdFromSecretMetadata(KeyVaultSecret secret)
    {
        if (secret.Properties.Tags is not null
            && secret.Properties.Tags.TryGetValue("appid", out var appIdTag)
            && !string.IsNullOrWhiteSpace(appIdTag))
        {
            return appIdTag.Trim();
        }

        var contentType = secret.Properties.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        const string prefix = "appid=";
        var index = contentType.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var value = contentType[(index + prefix.Length)..].Trim();
        if (value.Contains(';'))
            value = value.Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
