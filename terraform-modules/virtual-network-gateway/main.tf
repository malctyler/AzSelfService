terraform {
  required_version = ">= 1.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
  backend "azurerm" {}
}

provider "azurerm" {
  features {}
}

resource "azurerm_virtual_network_gateway" "this" {
  name                = var.name
  location            = var.location
  resource_group_name = var.resource_group_name
  type                = var.type
  sku                 = var.sku
  vpn_type            = var.vpn_type
  generation          = var.generation

  active_active                         = var.active_active
  bgp_enabled                           = var.bgp_enabled
  dns_forwarding_enabled                = var.dns_forwarding_enabled
  remote_vnet_traffic_enabled           = var.remote_vnet_traffic_enabled
  private_ip_address_enabled            = var.private_ip_address_enabled
  ip_sec_replay_protection_enabled      = var.ip_sec_replay_protection_enabled
  virtual_wan_traffic_enabled           = var.virtual_wan_traffic_enabled
  bgp_route_translation_for_nat_enabled = var.bgp_route_translation_for_nat_enabled

  ip_configuration {
    name                          = var.ip_configuration.name
    public_ip_address_id          = var.ip_configuration.public_ip_address_id
    private_ip_address_allocation = var.ip_configuration.private_ip_address_allocation
    subnet_id                     = var.ip_configuration.subnet_id
  }

  dynamic "bgp_settings" {
    for_each = var.bgp_enabled ? [var.bgp_settings] : []
    content {
      asn         = bgp_settings.value.asn
      peer_weight = bgp_settings.value.peer_weight

      peering_addresses {
        ip_configuration_name = bgp_settings.value.ip_configuration_name
        apipa_addresses       = bgp_settings.value.apipa_addresses
      }
    }
  }

  tags = var.tags
}
