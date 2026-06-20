terraform {
  required_version = ">= 1.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
  backend "azurerm" {}
}

provider "azurerm" {
  features {}
}

locals {
  # Parse the comma-separated dns_servers string into a list, filtering blanks.
  dns_servers_list = var.dns_servers == "" ? [] : [
    for s in split(",", var.dns_servers) : trimspace(s) if trimspace(s) != ""
  ]

  enable_nsg               = lower(trimspace(var.enable_nsg)) == "true"
  has_explicit_subnets     = length(var.subnets) > 0
  create_legacy_nsg        = local.enable_nsg && !local.has_explicit_subnets
  normalized_address_space = cidrsubnet(var.address_space, 0, 0)

  subnet_count = local.has_explicit_subnets ? 1 : tonumber(var.subnet_count)
  subnet_newbits = local.has_explicit_subnets ? 0 : (
    local.subnet_count <= 1 ? 0 : (
      local.subnet_count == 2 ? 1 : 2
    )
  )

  all_subnet_service_endpoints = [
    var.subnet_1_service_endpoints,
    var.subnet_2_service_endpoints,
    var.subnet_3_service_endpoints,
    var.subnet_4_service_endpoints
  ]

  generated_subnet_definitions = {
    for idx in range(local.subnet_count) : tostring(idx) => {
      name                        = idx == 0 ? "primary" : "${idx + 1}-of-${local.subnet_count}"
      prefix                      = cidrsubnet(local.normalized_address_space, local.subnet_newbits, idx)
      service_endpoints           = local.all_subnet_service_endpoints[idx]
      network_security_group_name = ""
      network_security_group_id   = ""
    }
  }

  explicit_subnet_definitions = {
    for idx, subnet in var.subnets : tostring(idx) => {
      name                        = subnet.name
      prefix                      = subnet.address_prefix
      service_endpoints           = subnet.service_endpoints
      network_security_group_name = trimspace(subnet.network_security_group_name)
      network_security_group_id   = trimspace(subnet.network_security_group_id)
    }
  }

  explicit_nsg_definitions = {
    for nsg in var.nsgs : nsg.name => {
      name           = nsg.name
      tags           = nsg.tags
      security_rules = nsg.security_rules
    }
  }

  subnet_definitions = local.has_explicit_subnets ? local.explicit_subnet_definitions : local.generated_subnet_definitions

  subnet_nsg_associations = {
    for key, subnet in local.subnet_definitions : key => subnet
    if subnet.network_security_group_name != "" || subnet.network_security_group_id != "" || local.create_legacy_nsg
  }

  common_tags = merge(
    var.tags,
    {
      ManagedBy = "AzSelfService"
      Module    = "virtual-network"
    }
  )
}

resource "azurerm_virtual_network" "this" {
  name                = var.name
  location            = var.location
  resource_group_name = var.resource_group_name
  address_space       = [local.normalized_address_space]
  dns_servers         = local.dns_servers_list
  tags                = local.common_tags
}

resource "azurerm_subnet" "this" {
  for_each             = local.subnet_definitions
  name                 = each.value.name
  resource_group_name  = var.resource_group_name
  virtual_network_name = azurerm_virtual_network.this.name
  address_prefixes     = [each.value.prefix]
  service_endpoints    = each.value.service_endpoints
}

resource "random_string" "nsg_suffix" {
  count   = local.create_legacy_nsg ? 1 : 0
  length  = 5
  upper   = false
  special = false
}

resource "azurerm_network_security_group" "primary" {
  count               = local.create_legacy_nsg ? 1 : 0
  name                = substr("${var.name}-primary-${random_string.nsg_suffix[0].result}-nsg", 0, 80)
  location            = var.location
  resource_group_name = var.resource_group_name
  tags                = local.common_tags
}

resource "azurerm_network_security_group" "explicit" {
  for_each            = local.explicit_nsg_definitions
  name                = each.value.name
  location            = var.location
  resource_group_name = var.resource_group_name
  tags                = merge(local.common_tags, each.value.tags)

  dynamic "security_rule" {
    for_each = each.value.security_rules
    content {
      name                                       = security_rule.value.name
      priority                                   = security_rule.value.priority
      direction                                  = security_rule.value.direction
      access                                     = security_rule.value.access
      protocol                                   = security_rule.value.protocol
      source_port_range                          = security_rule.value.source_port_range
      destination_port_range                     = security_rule.value.destination_port_range
      source_address_prefix                      = security_rule.value.source_address_prefix
      destination_address_prefix                 = security_rule.value.destination_address_prefix
      description                                = try(security_rule.value.description, null)
      source_port_ranges                         = try(security_rule.value.source_port_ranges, null)
      destination_port_ranges                    = try(security_rule.value.destination_port_ranges, null)
      source_address_prefixes                    = try(security_rule.value.source_address_prefixes, null)
      destination_address_prefixes               = try(security_rule.value.destination_address_prefixes, null)
      source_application_security_group_ids      = try(security_rule.value.source_application_security_group_ids, null)
      destination_application_security_group_ids = try(security_rule.value.destination_application_security_group_ids, null)
    }
  }
}

resource "azurerm_subnet_network_security_group_association" "primary" {
  for_each                  = local.subnet_nsg_associations
  subnet_id                 = azurerm_subnet.this[each.key].id
  network_security_group_id = each.value.network_security_group_name != "" ? azurerm_network_security_group.explicit[each.value.network_security_group_name].id : each.value.network_security_group_id != "" ? each.value.network_security_group_id : azurerm_network_security_group.primary[0].id
}
