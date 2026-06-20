using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using AzSelfService.API.Data.Entities;

namespace AzSelfService.API.Services;

public sealed record ImportResourceOption(
    string Name,
    string ResourceId,
    string Location,
    Dictionary<string, string> Tags,
    string Summary,
    string? ParentName = null);

public sealed record NetworkSecurityRuleImportInfo(
    string Name,
    string ResourceId,
    string ResourceGroupName,
    string NetworkSecurityGroupName,
    int Priority,
    string Direction,
    string Access,
    string Protocol,
    string Description,
    string SourcePortRange,
    string DestinationPortRange,
    string SourceAddressPrefix,
    string DestinationAddressPrefix,
    IReadOnlyList<string> SourcePortRanges,
    IReadOnlyList<string> DestinationPortRanges,
    IReadOnlyList<string> SourceAddressPrefixes,
    IReadOnlyList<string> DestinationAddressPrefixes,
    IReadOnlyList<string> SourceApplicationSecurityGroupIds,
    IReadOnlyList<string> DestinationApplicationSecurityGroupIds,
    Dictionary<string, string> Tags);

public sealed record NetworkSecurityGroupImportInfo(
    string Name,
    string ResourceId,
    string Location,
    string ResourceGroupName,
    IReadOnlyList<object> SecurityRules,
    Dictionary<string, string> Tags);

public sealed record PublicIpImportInfo(
    string Name,
    string ResourceId,
    string Location,
    string ResourceGroupName,
    string AllocationMethod,
    string Sku,
    string SkuTier,
    string IpVersion,
    int IdleTimeoutInMinutes,
    string DdosProtectionMode,
    IReadOnlyList<string> Zones,
    Dictionary<string, string> Tags);

public sealed record LocalNetworkGatewayImportInfo(
    string Name,
    string ResourceId,
    string Location,
    string ResourceGroupName,
    string GatewayAddress,
    string GatewayFqdn,
    IReadOnlyList<string> AddressSpace,
    Dictionary<string, string> Tags);

public sealed record VirtualNetworkGatewayImportInfo(
    string Name,
    string ResourceId,
    string Location,
    string ResourceGroupName,
    string Type,
    string Sku,
    string VpnType,
    string Generation,
    bool ActiveActive,
    bool BgpEnabled,
    bool DnsForwardingEnabled,
    bool RemoteVnetTrafficEnabled,
    bool PrivateIpAddressEnabled,
    bool IpSecReplayProtectionEnabled,
    bool VirtualWanTrafficEnabled,
    bool BgpRouteTranslationForNatEnabled,
    object IpConfiguration,
    object BgpSettings,
    Dictionary<string, string> Tags);

public sealed record VirtualNetworkPeeringImportInfo(
    string Name,
    string ResourceId,
    string ResourceGroupName,
    string VirtualNetworkName,
    string RemoteVirtualNetworkId,
    bool AllowVirtualNetworkAccess,
    bool AllowForwardedTraffic,
    bool AllowGatewayTransit,
    bool UseRemoteGateways,
    Dictionary<string, string> Tags);

public sealed record BastionHostImportInfo(
    string Name,
    string ResourceId,
    string Location,
    string ResourceGroupName,
    string Sku,
    string VirtualNetworkId,
    string IpConfigurationName,
    string SubnetId,
    string PublicIpAddressId,
    int ScaleUnits,
    bool CopyPasteEnabled,
    bool FileCopyEnabled,
    bool IpConnectEnabled,
    bool KerberosEnabled,
    bool SessionRecordingEnabled,
    bool ShareableLinkEnabled,
    bool TunnelingEnabled,
    IReadOnlyList<string> Zones,
    Dictionary<string, string> Tags);

public sealed record SubnetImportInfo(
    string Name,
    string ResourceId,
    string ResourceGroupName,
    string VirtualNetworkName,
    IReadOnlyList<string> AddressPrefixes,
    IReadOnlyList<string> ServiceEndpoints,
    bool DefaultOutboundAccessEnabled,
    string PrivateEndpointNetworkPolicies,
    bool PrivateLinkServiceNetworkPoliciesEnabled,
    Dictionary<string, string> Tags);

public sealed class ImportResourceDiscoveryService(
    IServiceProvider serviceProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<ImportResourceDiscoveryService> logger,
    ResourceGroupLookupService resourceGroupLookupService)
{
    private const string StorageAccountApiVersion = "2023-01-01";
    private const string KeyVaultApiVersion = "2023-07-01";
    private const string NetworkApiVersion = "2023-05-01";

    private static readonly HashSet<string> ManagedTagKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ManagedBy", "CreatedAt", "Environment"
    };

    public async Task<IReadOnlyList<ImportResourceOption>> ListImportOptionsAsync(
        CustomerEntity customer,
        string moduleName,
        string resourceGroupName,
        ISet<string> managedResourceIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ImportResourceOption> options = NormalizeModuleName(moduleName) switch
        {
            "storage-account" => (await resourceGroupLookupService.ListStorageAccountsAsync(customer, resourceGroupName, cancellationToken)).Accounts?
                .Select(a => new ImportResourceOption(a.Name, a.ResourceId, a.Location, a.Tags, a.Location))
                .ToList() ?? (IReadOnlyList<ImportResourceOption>)Array.Empty<ImportResourceOption>(),
            "keyvault" => (await resourceGroupLookupService.ListKeyVaultsAsync(customer, resourceGroupName, cancellationToken)).Accounts?
                .Select(v => new ImportResourceOption(v.Name, v.ResourceId, v.Location, v.Tags, v.Location))
                .ToList() ?? (IReadOnlyList<ImportResourceOption>)Array.Empty<ImportResourceOption>(),
            "virtual-network" => (await resourceGroupLookupService.ListVirtualNetworksAsync(customer, resourceGroupName, cancellationToken)).VNets?
                .Select(v => new ImportResourceOption(v.Name, v.ResourceId, v.Location, v.Tags, $"{v.AddressSpace}, {v.Subnets.Count} subnet{(v.Subnets.Count == 1 ? string.Empty : "s")}"))
                .ToList() ?? (IReadOnlyList<ImportResourceOption>)Array.Empty<ImportResourceOption>(),
            "network-security-group" => await ListNetworkSecurityGroupsAsync(customer, resourceGroupName, cancellationToken),
            "network-security-rule" => await ListNetworkSecurityRulesAsync(customer, resourceGroupName, cancellationToken),
            "public-ip" => await ListPublicIpsAsync(customer, resourceGroupName, cancellationToken),
            "local-network-gateway" => await ListLocalNetworkGatewaysAsync(customer, resourceGroupName, cancellationToken),
            "virtual-network-gateway" => await ListVirtualNetworkGatewaysAsync(customer, resourceGroupName, cancellationToken),
            "virtual-network-peering" => await ListVirtualNetworkPeeringsAsync(customer, resourceGroupName, cancellationToken),
            "bastion-host" => await ListBastionHostsAsync(customer, resourceGroupName, cancellationToken),
            "subnet" => await ListSubnetsAsync(customer, resourceGroupName, cancellationToken),
            _ => Array.Empty<ImportResourceOption>()
        };

        return options
            .Where(o => !managedResourceIds.Contains(o.ResourceId))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<NetworkSecurityGroupImportInfo?> LookupNetworkSecurityGroupAsync(CustomerEntity customer, string resourceGroupName, string name, CancellationToken cancellationToken)
    {
        var url = BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, $"networkSecurityGroups/{Uri.EscapeDataString(name)}");
        var content = await GetArmContentOrThrowAsync(customer, url, $"network security group '{name}'", cancellationToken);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        var rules = new List<object>();
        if (TryGetProperty(root, "properties", out var props)
            && TryGetProperty(props, "securityRules", out var rulesEl)
            && rulesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in rulesEl.EnumerateArray())
            {
                if (!TryGetProperty(rule, "name", out var nameEl))
                {
                    continue;
                }

                var ruleProps = TryGetProperty(rule, "properties", out var rp) ? rp : default;
                rules.Add(new
                {
                    name = nameEl.GetString() ?? string.Empty,
                    priority = GetInt(ruleProps, "priority"),
                    direction = GetString(ruleProps, "direction"),
                    access = GetString(ruleProps, "access"),
                    protocol = GetString(ruleProps, "protocol"),
                    source_port_range = GetString(ruleProps, "sourcePortRange", "*"),
                    destination_port_range = GetString(ruleProps, "destinationPortRange", "*"),
                    source_address_prefix = GetString(ruleProps, "sourceAddressPrefix", "*"),
                    destination_address_prefix = GetString(ruleProps, "destinationAddressPrefix", "*"),
                    description = GetString(ruleProps, "description"),
                    source_port_ranges = GetStringArray(ruleProps, "sourcePortRanges"),
                    destination_port_ranges = GetStringArray(ruleProps, "destinationPortRanges"),
                    source_address_prefixes = GetStringArray(ruleProps, "sourceAddressPrefixes"),
                    destination_address_prefixes = GetStringArray(ruleProps, "destinationAddressPrefixes"),
                    source_application_security_group_ids = GetResourceIdArray(ruleProps, "sourceApplicationSecurityGroups"),
                    destination_application_security_group_ids = GetResourceIdArray(ruleProps, "destinationApplicationSecurityGroups")
                });
            }
        }

        return new NetworkSecurityGroupImportInfo(
            name,
            GetString(root, "id"),
            GetString(root, "location"),
            resourceGroupName,
            rules,
            GetTags(root));
    }

    public async Task<NetworkSecurityRuleImportInfo?> LookupNetworkSecurityRuleAsync(CustomerEntity customer, string resourceGroupName, string networkSecurityGroupName, string name, CancellationToken cancellationToken)
    {
        var url = BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, $"networkSecurityGroups/{Uri.EscapeDataString(networkSecurityGroupName)}/securityRules/{Uri.EscapeDataString(name)}");
        var content = await GetArmContentOrThrowAsync(customer, url, $"network security rule '{name}'", cancellationToken);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var props = TryGetProperty(root, "properties", out var propsEl) ? propsEl : default;

        return new NetworkSecurityRuleImportInfo(
            name,
            GetString(root, "id"),
            resourceGroupName,
            networkSecurityGroupName,
            GetInt(props, "priority"),
            GetString(props, "direction"),
            GetString(props, "access"),
            GetString(props, "protocol"),
            GetString(props, "description"),
            GetString(props, "sourcePortRange", "*"),
            GetString(props, "destinationPortRange", "*"),
            GetString(props, "sourceAddressPrefix", "*"),
            GetString(props, "destinationAddressPrefix", "*"),
            GetStringArray(props, "sourcePortRanges"),
            GetStringArray(props, "destinationPortRanges"),
            GetStringArray(props, "sourceAddressPrefixes"),
            GetStringArray(props, "destinationAddressPrefixes"),
            GetResourceIdArray(props, "sourceApplicationSecurityGroups"),
            GetResourceIdArray(props, "destinationApplicationSecurityGroups"),
            GetTags(root));
    }

    public async Task<PublicIpImportInfo?> LookupPublicIpAsync(CustomerEntity customer, string resourceGroupName, string name, CancellationToken cancellationToken)
    {
        var url = BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, $"publicIPAddresses/{Uri.EscapeDataString(name)}");
        var content = await GetArmContentOrThrowAsync(customer, url, $"public IP '{name}'", cancellationToken);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var props = TryGetProperty(root, "properties", out var propsEl) ? propsEl : default;
        var sku = TryGetProperty(root, "sku", out var skuEl) ? skuEl : default;

        return new PublicIpImportInfo(
            name,
            GetString(root, "id"),
            GetString(root, "location"),
            resourceGroupName,
            GetString(props, "publicIPAllocationMethod", "Dynamic"),
            GetString(sku, "name", "Basic"),
            GetString(sku, "tier", "Regional"),
            GetString(props, "publicIPAddressVersion", "IPv4"),
            GetInt(props, "idleTimeoutInMinutes", 4),
            GetString(props, "ddosSettings", "protectionMode", "VirtualNetworkInherited"),
            GetStringArray(root, "zones"),
            GetTags(root));
    }

    public async Task<LocalNetworkGatewayImportInfo?> LookupLocalNetworkGatewayAsync(CustomerEntity customer, string resourceGroupName, string name, CancellationToken cancellationToken)
    {
        var url = BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, $"localNetworkGateways/{Uri.EscapeDataString(name)}");
        var content = await GetArmContentOrThrowAsync(customer, url, $"local network gateway '{name}'", cancellationToken);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var props = TryGetProperty(root, "properties", out var propsEl) ? propsEl : default;

        return new LocalNetworkGatewayImportInfo(
            name,
            GetString(root, "id"),
            GetString(root, "location"),
            resourceGroupName,
            GetString(props, "gatewayIpAddress"),
            GetString(props, "fqdn"),
            GetStringArray(props, "localNetworkAddressSpace", "addressPrefixes"),
            GetTags(root));
    }

    public async Task<VirtualNetworkGatewayImportInfo?> LookupVirtualNetworkGatewayAsync(CustomerEntity customer, string resourceGroupName, string name, CancellationToken cancellationToken)
    {
        var url = BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, $"virtualNetworkGateways/{Uri.EscapeDataString(name)}");
        var content = await GetArmContentOrThrowAsync(customer, url, $"virtual network gateway '{name}'", cancellationToken);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var props = TryGetProperty(root, "properties", out var propsEl) ? propsEl : default;
        var ipConfig = GetFirstArrayItem(props, "ipConfigurations");
        var ipConfigProps = TryGetProperty(ipConfig, "properties", out var ipConfigPropsEl) ? ipConfigPropsEl : default;
        var bgpSettings = TryGetProperty(props, "bgpSettings", out var bgpEl) ? bgpEl : default;
        var bgpPeeringAddress = GetFirstArrayItem(bgpSettings, "bgpPeeringAddresses");

        return new VirtualNetworkGatewayImportInfo(
            name,
            GetString(root, "id"),
            GetString(root, "location"),
            resourceGroupName,
            GetString(props, "gatewayType", "Vpn"),
            GetString(props, "sku", "name", "Basic"),
            GetString(props, "vpnType", "RouteBased"),
            GetString(props, "vpnGatewayGeneration", "Generation1"),
            GetBool(props, "activeActive", false),
            GetBool(props, "enableBgp", false),
            GetBool(props, "enableDnsForwarding", false),
            GetBool(props, "enablePrivateIpAddress", false),
            GetBool(props, "ipSecReplayProtectionEnabled", true),
            GetBool(props, "allowRemoteVnetTraffic", false),
            GetBool(props, "allowVirtualWanTraffic", false),
            GetBool(props, "bgpRouteTranslationForNatEnabled", false),
            new
            {
                name = GetString(ipConfig, "name", "default"),
                public_ip_address_id = GetString(ipConfigProps, "publicIPAddress", "id"),
                private_ip_address_allocation = GetString(ipConfigProps, "privateIPAllocationMethod", "Dynamic"),
                subnet_id = GetString(ipConfigProps, "subnet", "id")
            },
            new
            {
                asn = GetInt(bgpSettings, "asn", 65515),
                peer_weight = GetInt(bgpSettings, "peerWeight", 0),
                ip_configuration_name = GetString(bgpPeeringAddress, "ipconfigurationId", string.Empty).Split('/').LastOrDefault() ?? "default",
                apipa_addresses = GetStringArray(bgpPeeringAddress, "defaultBgpIpAddresses")
            },
            GetTags(root));
    }

    public async Task<VirtualNetworkPeeringImportInfo?> LookupVirtualNetworkPeeringAsync(CustomerEntity customer, string resourceGroupName, string virtualNetworkName, string name, CancellationToken cancellationToken)
    {
        var url = BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, $"virtualNetworks/{Uri.EscapeDataString(virtualNetworkName)}/virtualNetworkPeerings/{Uri.EscapeDataString(name)}");
        var content = await GetArmContentOrThrowAsync(customer, url, $"virtual network peering '{name}'", cancellationToken);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var props = TryGetProperty(root, "properties", out var propsEl) ? propsEl : default;

        return new VirtualNetworkPeeringImportInfo(
            name,
            GetString(root, "id"),
            resourceGroupName,
            virtualNetworkName,
            GetString(props, "remoteVirtualNetwork", "id"),
            GetBool(props, "allowVirtualNetworkAccess", true),
            GetBool(props, "allowForwardedTraffic", true),
            GetBool(props, "allowGatewayTransit", false),
            GetBool(props, "useRemoteGateways", false),
            GetTags(root));
    }

    public async Task<BastionHostImportInfo?> LookupBastionHostAsync(CustomerEntity customer, string resourceGroupName, string name, CancellationToken cancellationToken)
    {
        var url = BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, $"bastionHosts/{Uri.EscapeDataString(name)}");
        var content = await GetArmContentOrThrowAsync(customer, url, $"bastion host '{name}'", cancellationToken);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var props = TryGetProperty(root, "properties", out var propsEl) ? propsEl : default;
        var ipConfig = GetFirstArrayItem(props, "ipConfigurations");
        var ipConfigProps = TryGetProperty(ipConfig, "properties", out var ipConfigPropsEl) ? ipConfigPropsEl : default;

        return new BastionHostImportInfo(
            name,
            GetString(root, "id"),
            GetString(root, "location"),
            resourceGroupName,
            GetString(props, "sku", "name", GetString(root, "sku", "name", "Developer")),
            GetString(props, "virtualNetwork", "id"),
            GetString(ipConfig, "name", "bastion-ip-config"),
            GetString(ipConfigProps, "subnet", "id"),
            GetString(ipConfigProps, "publicIPAddress", "id"),
            GetInt(props, "scaleUnits", 2),
            GetBool(props, "enableCopyPaste", true),
            GetBool(props, "enableFileCopy", false),
            GetBool(props, "enableIpConnect", false),
            GetBool(props, "enableKerberos", false),
            GetBool(props, "enableSessionRecording", false),
            GetBool(props, "enableShareableLink", false),
            GetBool(props, "enableTunneling", false),
            GetStringArray(root, "zones"),
            GetTags(root));
    }

    public async Task<SubnetImportInfo?> LookupSubnetAsync(CustomerEntity customer, string resourceGroupName, string virtualNetworkName, string name, CancellationToken cancellationToken)
    {
        var url = BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, $"virtualNetworks/{Uri.EscapeDataString(virtualNetworkName)}/subnets/{Uri.EscapeDataString(name)}");
        var content = await GetArmContentOrThrowAsync(customer, url, $"subnet '{name}'", cancellationToken);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var props = TryGetProperty(root, "properties", out var propsEl) ? propsEl : default;

        var addressPrefixes = GetStringArray(props, "addressPrefixes");
        if (addressPrefixes.Count == 0)
        {
            var prefix = GetString(props, "addressPrefix");
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                addressPrefixes = new[] { prefix };
            }
        }

        return new SubnetImportInfo(
            name,
            GetString(root, "id"),
            resourceGroupName,
            virtualNetworkName,
            addressPrefixes,
            GetStringArray(props, "serviceEndpoints", "service"),
            GetBool(props, "defaultOutboundAccess", true),
            GetString(props, "privateEndpointNetworkPolicies", "Enabled"),
            GetBool(props, "privateLinkServiceNetworkPolicies", true),
            GetTags(root));
    }

    private async Task<IReadOnlyList<ImportResourceOption>> ListNetworkSecurityGroupsAsync(CustomerEntity customer, string resourceGroupName, CancellationToken cancellationToken)
    {
        var content = await GetArmContentOrThrowAsync(customer, BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, "networkSecurityGroups"), "network security groups", cancellationToken);
        using var document = JsonDocument.Parse(content);
        return EnumerateValueArray(document.RootElement)
            .Select(item => new ImportResourceOption(
                GetString(item, "name"),
                GetString(item, "id"),
                GetString(item, "location"),
                GetTags(item),
                $"{CountArray(TryGetProperty(item, "properties", out var props) ? props : default, "securityRules")} rule(s)"))
            .Where(o => !string.IsNullOrWhiteSpace(o.Name) && !string.IsNullOrWhiteSpace(o.ResourceId))
            .ToList();
    }

    private async Task<IReadOnlyList<ImportResourceOption>> ListNetworkSecurityRulesAsync(CustomerEntity customer, string resourceGroupName, CancellationToken cancellationToken)
    {
        var content = await GetArmContentOrThrowAsync(customer, BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, "networkSecurityGroups"), "network security rules", cancellationToken);
        using var document = JsonDocument.Parse(content);
        var results = new List<ImportResourceOption>();

        foreach (var nsg in EnumerateValueArray(document.RootElement))
        {
            var nsgName = GetString(nsg, "name");
            var nsgTags = GetTags(nsg);
            var props = TryGetProperty(nsg, "properties", out var propsEl) ? propsEl : default;
            if (!TryGetProperty(props, "securityRules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var rule in rules.EnumerateArray())
            {
                var ruleProps = TryGetProperty(rule, "properties", out var rulePropsEl) ? rulePropsEl : default;
                results.Add(new ImportResourceOption(
                    GetString(rule, "name"),
                    GetString(rule, "id"),
                    GetString(nsg, "location"),
                    nsgTags,
                    $"{GetString(ruleProps, "direction")} {GetString(ruleProps, "access")} {GetString(ruleProps, "protocol")} priority {GetInt(ruleProps, "priority")}",
                    nsgName));
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<ImportResourceOption>> ListPublicIpsAsync(CustomerEntity customer, string resourceGroupName, CancellationToken cancellationToken)
    {
        var content = await GetArmContentOrThrowAsync(customer, BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, "publicIPAddresses"), "public IPs", cancellationToken);
        using var document = JsonDocument.Parse(content);
        return EnumerateValueArray(document.RootElement)
            .Select(item =>
            {
                var props = TryGetProperty(item, "properties", out var propsEl) ? propsEl : default;
                return new ImportResourceOption(
                    GetString(item, "name"),
                    GetString(item, "id"),
                    GetString(item, "location"),
                    GetTags(item),
                    $"{GetString(props, "publicIPAllocationMethod", "Dynamic")} {GetString(props, "publicIPAddressVersion", "IPv4")}");
            })
            .Where(o => !string.IsNullOrWhiteSpace(o.Name) && !string.IsNullOrWhiteSpace(o.ResourceId))
            .ToList();
    }

    private async Task<IReadOnlyList<ImportResourceOption>> ListLocalNetworkGatewaysAsync(CustomerEntity customer, string resourceGroupName, CancellationToken cancellationToken)
    {
        var content = await GetArmContentOrThrowAsync(customer, BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, "localNetworkGateways"), "local network gateways", cancellationToken);
        using var document = JsonDocument.Parse(content);
        return EnumerateValueArray(document.RootElement)
            .Select(item =>
            {
                var props = TryGetProperty(item, "properties", out var propsEl) ? propsEl : default;
                return new ImportResourceOption(
                    GetString(item, "name"),
                    GetString(item, "id"),
                    GetString(item, "location"),
                    GetTags(item),
                    GetString(props, "gatewayIpAddress"));
            })
            .Where(o => !string.IsNullOrWhiteSpace(o.Name) && !string.IsNullOrWhiteSpace(o.ResourceId))
            .ToList();
    }

    private async Task<IReadOnlyList<ImportResourceOption>> ListVirtualNetworkGatewaysAsync(CustomerEntity customer, string resourceGroupName, CancellationToken cancellationToken)
    {
        var content = await GetArmContentOrThrowAsync(customer, BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, "virtualNetworkGateways"), "virtual network gateways", cancellationToken);
        using var document = JsonDocument.Parse(content);
        return EnumerateValueArray(document.RootElement)
            .Select(item =>
            {
                var props = TryGetProperty(item, "properties", out var propsEl) ? propsEl : default;
                return new ImportResourceOption(
                    GetString(item, "name"),
                    GetString(item, "id"),
                    GetString(item, "location"),
                    GetTags(item),
                    $"{GetString(props, "gatewayType", "Vpn")} {GetString(props, "sku", "name", "Basic")}");
            })
            .Where(o => !string.IsNullOrWhiteSpace(o.Name) && !string.IsNullOrWhiteSpace(o.ResourceId))
            .ToList();
    }

    private async Task<IReadOnlyList<ImportResourceOption>> ListVirtualNetworkPeeringsAsync(CustomerEntity customer, string resourceGroupName, CancellationToken cancellationToken)
    {
        var content = await GetArmContentOrThrowAsync(customer, BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, "virtualNetworks"), "virtual network peerings", cancellationToken);
        using var document = JsonDocument.Parse(content);
        var results = new List<ImportResourceOption>();

        foreach (var vnet in EnumerateValueArray(document.RootElement))
        {
            var vnetName = GetString(vnet, "name");
            var location = GetString(vnet, "location");
            var tags = GetTags(vnet);
            var props = TryGetProperty(vnet, "properties", out var propsEl) ? propsEl : default;
            if (!TryGetProperty(props, "virtualNetworkPeerings", out var peerings) || peerings.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var peering in peerings.EnumerateArray())
            {
                var peeringProps = TryGetProperty(peering, "properties", out var peeringPropsEl) ? peeringPropsEl : default;
                results.Add(new ImportResourceOption(
                    GetString(peering, "name"),
                    GetString(peering, "id"),
                    location,
                    tags,
                    GetString(peeringProps, "remoteVirtualNetwork", "id"),
                    vnetName));
            }
        }

        return results;
    }

    private async Task<IReadOnlyList<ImportResourceOption>> ListBastionHostsAsync(CustomerEntity customer, string resourceGroupName, CancellationToken cancellationToken)
    {
        var content = await GetArmContentOrThrowAsync(customer, BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, "bastionHosts"), "bastion hosts", cancellationToken);
        using var document = JsonDocument.Parse(content);
        return EnumerateValueArray(document.RootElement)
            .Select(item =>
            {
                var props = TryGetProperty(item, "properties", out var propsEl) ? propsEl : default;
                return new ImportResourceOption(
                    GetString(item, "name"),
                    GetString(item, "id"),
                    GetString(item, "location"),
                    GetTags(item),
                    GetString(props, "sku", "name", GetString(item, "sku", "name", "Developer")));
            })
            .Where(o => !string.IsNullOrWhiteSpace(o.Name) && !string.IsNullOrWhiteSpace(o.ResourceId))
            .ToList();
    }

    private async Task<IReadOnlyList<ImportResourceOption>> ListSubnetsAsync(CustomerEntity customer, string resourceGroupName, CancellationToken cancellationToken)
    {
        var content = await GetArmContentOrThrowAsync(customer, BuildNetworkUrl(customer.SubscriptionId!, resourceGroupName, "virtualNetworks"), "subnets", cancellationToken);
        using var document = JsonDocument.Parse(content);
        var results = new List<ImportResourceOption>();

        foreach (var vnet in EnumerateValueArray(document.RootElement))
        {
            var vnetName = GetString(vnet, "name");
            var location = GetString(vnet, "location");
            var tags = GetTags(vnet);
            var props = TryGetProperty(vnet, "properties", out var propsEl) ? propsEl : default;
            if (!TryGetProperty(props, "subnets", out var subnets) || subnets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var subnet in subnets.EnumerateArray())
            {
                var subnetProps = TryGetProperty(subnet, "properties", out var subnetPropsEl) ? subnetPropsEl : default;
                var prefixes = GetStringArray(subnetProps, "addressPrefixes");
                var prefixSummary = prefixes.Count > 0 ? string.Join(", ", prefixes) : GetString(subnetProps, "addressPrefix");
                results.Add(new ImportResourceOption(
                    GetString(subnet, "name"),
                    GetString(subnet, "id"),
                    location,
                    tags,
                    prefixSummary,
                    vnetName));
            }
        }

        return results;
    }

    private async Task<(string Token, string? ErrorMessage)> GetArmAccessTokenAsync(CustomerEntity customer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customer.TenantId) || string.IsNullOrWhiteSpace(customer.SubscriptionId))
            return (string.Empty, "Customer tenant_id and subscription_id must be configured.");
        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef))
            return (string.Empty, "Customer service principal client secret reference is not configured.");

        var secretClient = serviceProvider.GetService<SecretClient>();
        if (secretClient is null)
            return (string.Empty, "Key Vault client is not configured on the API service.");

        try
        {
            var secretName = ResolveSecretReference(customer.SpClientSecretSecretRef);
            var secretResponse = await secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            var secret = secretResponse.Value;

            if (string.IsNullOrWhiteSpace(secret.Value))
                return (string.Empty, "Customer service principal secret is empty.");

            var appId = ResolveAppIdFromSecretMetadata(secret);
            if (string.IsNullOrWhiteSpace(appId))
                return (string.Empty, "Could not resolve appid from Key Vault secret metadata.");

            var credential = new ClientSecretCredential(customer.TenantId, appId, secret.Value);
            var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { "https://management.azure.com/.default" }), cancellationToken);
            return (token.Token, null);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return (string.Empty, "Customer service principal secret was not found in Key Vault.");
        }
    }

    private async Task<string> GetArmContentOrThrowAsync(CustomerEntity customer, string url, string resourceDescription, CancellationToken cancellationToken)
    {
        var (token, errorMessage) = await GetArmAccessTokenAsync(customer, cancellationToken);
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new InvalidOperationException(errorMessage);
        }

        var httpClient = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"Could not find {resourceDescription}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("ARM import discovery failed for {Resource}. Status={Status}, Body={Body}", resourceDescription, (int)response.StatusCode, errorBody);
            throw new InvalidOperationException($"Could not look up {resourceDescription} at this time.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static IEnumerable<JsonElement> EnumerateValueArray(JsonElement root)
    {
        if (TryGetProperty(root, "value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                yield return item;
            }
        }
    }

    private static string BuildNetworkUrl(string subscriptionId, string resourceGroupName, string resourceTypePath)
        => $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}/providers/Microsoft.Network/{resourceTypePath}?api-version={NetworkApiVersion}";

    private static string NormalizeModuleName(string moduleName) => moduleName.Trim().ToLowerInvariant();

    private static Dictionary<string, string> GetTags(JsonElement root)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (TryGetProperty(root, "tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in tagsEl.EnumerateObject())
            {
                if (!ManagedTagKeys.Contains(tag.Name) && tag.Value.ValueKind == JsonValueKind.String)
                {
                    tags[tag.Name] = tag.Value.GetString() ?? string.Empty;
                }
            }
        }

        return tags;
    }

    private static string GetString(JsonElement root, string propertyName, string defaultValue = "")
        => TryGetProperty(root, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? defaultValue
            : defaultValue;

    private static string GetString(JsonElement root, string objectPropertyName, string nestedPropertyName, string defaultValue = "")
    {
        if (!TryGetProperty(root, objectPropertyName, out var nested))
        {
            return defaultValue;
        }

        return GetString(nested, nestedPropertyName, defaultValue);
    }

    private static int GetInt(JsonElement root, string propertyName, int defaultValue = 0)
        => TryGetProperty(root, propertyName, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : defaultValue;

    private static bool GetBool(JsonElement root, string propertyName, bool defaultValue)
        => TryGetProperty(root, propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string arrayPropertyName, string objectStringPropertyName)
    {
        if (!TryGetProperty(root, arrayPropertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text);
                }
                continue;
            }

            if (item.ValueKind == JsonValueKind.Object && TryGetProperty(item, objectStringPropertyName, out var nested) && nested.ValueKind == JsonValueKind.String)
            {
                var text = nested.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    list.Add(text);
                }
            }
        }

        return list;
    }

    private static IReadOnlyList<string> GetResourceIdArray(JsonElement root, string propertyName)
        => GetStringArray(root, propertyName, "id");

    private static JsonElement GetFirstArrayItem(JsonElement root, string propertyName)
    {
        if (TryGetProperty(root, propertyName, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                return item;
            }
        }

        return default;
    }

    private static int CountArray(JsonElement root, string propertyName)
        => TryGetProperty(root, propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;

    private static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string ResolveSecretReference(string reference)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase))
            {
                return segments[1];
            }

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
        {
            return null;
        }

        const string prefix = "appid=";
        var index = contentType.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = contentType[(index + prefix.Length)..].Trim();
        if (value.Contains(';'))
        {
            value = value.Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}