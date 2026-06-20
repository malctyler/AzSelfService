output "vnet_id" {
  description = "ID of the created virtual network."
  value       = azurerm_virtual_network.this.id
}

output "vnet_name" {
  description = "Name of the created virtual network."
  value       = azurerm_virtual_network.this.name
}

output "vnet_address_space" {
  description = "Address space of the virtual network."
  value       = azurerm_virtual_network.this.address_space[0]
}

output "subnet_id" {
  description = "ID of the primary subnet."
  value       = azurerm_subnet.this["0"].id
}

output "subnet_name" {
  description = "Name of the primary subnet."
  value       = azurerm_subnet.this["0"].name
}

output "subnet_details" {
  description = "Details for all generated subnets in this virtual network."
  value = [
    for key, subnet in azurerm_subnet.this : {
      name              = subnet.name
      id                = subnet.id
      address_prefix    = subnet.address_prefixes[0]
      service_endpoints = subnet.service_endpoints
      nsg_associated    = contains(keys(azurerm_subnet_network_security_group_association.primary), key)
    }
  ]
}

output "subnet_nsg_associations" {
  description = "NSG association IDs by subnet index key for all managed associations."
  value = {
    for key, assoc in azurerm_subnet_network_security_group_association.primary :
    key => assoc.id
  }
}

output "nsg_ids" {
  description = "IDs for NSGs managed by this module (explicit and legacy generated)."
  value = merge(
    { for name, nsg in azurerm_network_security_group.explicit : name => nsg.id },
    local.create_legacy_nsg ? { legacy_primary = azurerm_network_security_group.primary[0].id } : {}
  )
}

output "nsg_id" {
  description = "ID of the legacy module-created NSG. Empty string when using explicit subnet-level NSG IDs or NSG creation is disabled."
  value       = local.create_legacy_nsg ? azurerm_network_security_group.primary[0].id : ""
}
