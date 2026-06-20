terraform {
  required_version = ">= 1.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.0"
    }
  }
  backend "azurerm" {}
}

provider "azurerm" {
  features {}
}

locals {
  vm_size_by_sku = {
    b2s    = "Standard_B2s"
    d2s_v5 = "Standard_D2s_v5"
    d4s_v5 = "Standard_D4s_v5"
  }

  vm_size = local.vm_size_by_sku[var.sku]

  # Fall back to allow-all if the user left rdp_allowed_cidr blank
  rdp_cidr = trimspace(var.rdp_allowed_cidr) != "" ? trimspace(var.rdp_allowed_cidr) : "0.0.0.0/0"

  # Domain join is triggered by a non-empty domain_name
  domain_join_enabled = trimspace(var.domain_name) != ""

  # Post-install is triggered by a non-empty package list OR an external script URI.
  use_script_uri       = trimspace(var.post_install_script_uri) != ""
  post_install_enabled = local.use_script_uri || length(var.chocolatey_packages) > 0

  # When true, the VM's system-assigned managed identity fetches the script blob
  # via an IMDS bearer token — no storage account key required.
  # var is string (not bool) to tolerate "" from the portal when the field is hidden.
  use_mi_download = var.post_install_use_managed_identity == "true" && local.use_script_uri

  # Strip query-string before extracting filename for commandToExecute (SAS mode).
  script_uri_path     = local.use_script_uri ? split("?", var.post_install_script_uri)[0] : ""
  script_uri_filename = local.use_script_uri ? basename(local.script_uri_path) : ""

  # Inline Chocolatey script with strict error handling.
  # $ErrorActionPreference = 'Stop' + try/catch ensures a non-zero exit code is
  # returned to the CSE on any failure, which surfaces as a provisioning error
  # on the VM's Extensions blade in the Azure portal.
  #
  # When the VM is domain-joined, the NIC DNS is set to DC IP(s) which typically
  # only resolve internal names. Public DNS servers (8.8.8.8, 1.1.1.1) are
  # appended to the existing list so Chocolatey can reach community.chocolatey.org
  # while domain resolution continues to work via the DC IPs.
  post_install_inline_script = join("\r\n", concat(
    [
      "$ProgressPreference = 'SilentlyContinue'",
      "$ErrorActionPreference = 'Stop'",
      "try {",
      "  try {",
      "    $cfg = Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object { $_.ServerAddresses -and $_.ServerAddresses.Count -gt 0 } | Select-Object -First 1",
      "    if ($null -ne $cfg) {",
      "      $dns = @($cfg.ServerAddresses) + @('8.8.8.8','1.1.1.1') | Select-Object -Unique",
      "      Set-DnsClientServerAddress -InterfaceIndex $cfg.InterfaceIndex -ServerAddresses $dns -ErrorAction Stop",
      "    }",
      "  } catch {",
      "    [Console]::WriteLine('DNS pre-check skipped: ' + $_.Exception.Message)",
      "  }",
      "  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12",
      "  Set-ExecutionPolicy Bypass -Scope Process -Force",
      "  iex ((New-Object Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))",
    ],
    [for pkg in var.chocolatey_packages : "  choco install ${pkg} -y --no-progress --fail-on-error"],
    [
      "} catch {",
      "  [Console]::Error.WriteLine($_.Exception.Message)",
      "  exit 1",
      "}",
    ]
  ))

  # Managed identity download script: acquires a bearer token from IMDS then
  # downloads the blob using that token — no storage account key ever required.
  # The VM's principal_id output must have Storage Blob Data Reader on the container.
  cse_mi_script = join("\r\n", [
    "$$ProgressPreference = 'SilentlyContinue'",
    "$$ErrorActionPreference = 'Stop'",
    "try {",
    "  $$token = (Invoke-RestMethod 'http://169.254.169.254/metadata/identity/oauth2/token?api-version=2018-02-01&resource=https://storage.azure.com/' -Headers @{Metadata='true'}).access_token",
    "  Invoke-WebRequest -Uri '${var.post_install_script_uri}' -Headers @{Authorization=\"Bearer $$token\"; 'x-ms-version' = '2020-04-08'} -OutFile 'C:\\Windows\\Temp\\post_install.ps1'",
    "  & 'C:\\Windows\\Temp\\post_install.ps1'",
    "} catch {",
    "  [Console]::Error.WriteLine($$_.Exception.Message)",
    "  exit 1",
    "}",
  ])

  # CSE settings — three modes, none of which use a storage account key:
  #  1. SAS/public URI       → fileUris + commandToExecute (CSE native HTTPS download)
  #  2. Managed identity URI → -EncodedCommand: UTF-16LE base64 of an iex() decoder
  #  3. Chocolatey only      → same as mode 2
  #
  # Modes 2 and 3 use -EncodedCommand so the commandToExecute string contains only
  # alphanumeric characters and the base64 alphabet (A-Za-z0-9+/=). This makes it
  # completely safe for cmd.exe — no semicolons, no :: operators, no parentheses,
  # no quoting issues. The -Command approach broke because cmd.exe passes the value
  # through its own parser before handing it to powershell.exe.
  #
  # textencodebase64(..., "UTF-16LE") converts the inner decoder script to UTF-16LE
  # base64, which is exactly what powershell.exe -EncodedCommand requires.
  cse_payload_b64 = base64encode(local.use_mi_download ? local.cse_mi_script : local.post_install_inline_script)

  # The inner command (pure ASCII) decodes the UTF-8 payload and runs it inline.
  cse_encoded_cmd = textencodebase64(
    "iex([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${local.cse_payload_b64}')))",
    "UTF-16LE"
  )

  cse_inline_command = "powershell -NonInteractive -EncodedCommand ${local.cse_encoded_cmd}"

  cse_settings = (
    local.use_script_uri && !local.use_mi_download
    ? jsonencode({
      fileUris         = [var.post_install_script_uri]
      commandToExecute = "powershell -ExecutionPolicy Unrestricted -NonInteractive -File ${local.script_uri_filename}"
    })
    : jsonencode({
      commandToExecute = local.cse_inline_command
    })
  )

  common_tags = merge(
    var.tags,
    {
      ManagedBy = "AzSelfService"
      Module    = "windows-server-marketplace"
    }
  )
}

resource "azurerm_public_ip" "this" {
  name                = "${var.name}-pip"
  location            = var.location
  resource_group_name = var.resource_group_name
  allocation_method   = "Static"
  sku                 = "Standard"
  tags                = local.common_tags
}

resource "azurerm_network_security_group" "this" {
  name                = "${var.name}-nsg"
  location            = var.location
  resource_group_name = var.resource_group_name
  tags                = local.common_tags
}

resource "azurerm_network_security_rule" "allow_rdp" {
  name                        = "allow-rdp"
  priority                    = 1000
  direction                   = "Inbound"
  access                      = "Allow"
  protocol                    = "Tcp"
  source_port_range           = "*"
  destination_port_range      = "3389"
  source_address_prefix       = local.rdp_cidr
  destination_address_prefix  = "*"
  resource_group_name         = var.resource_group_name
  network_security_group_name = azurerm_network_security_group.this.name
}

resource "azurerm_network_interface" "this" {
  name                = "${var.name}-nic"
  location            = var.location
  resource_group_name = var.resource_group_name
  dns_servers         = length(var.dns_servers) > 0 ? var.dns_servers : null

  ip_configuration {
    name                          = "ipconfig1"
    subnet_id                     = var.subnet_id
    private_ip_address_allocation = "Dynamic"
    public_ip_address_id          = azurerm_public_ip.this.id
  }

  tags = local.common_tags
}

resource "azurerm_network_interface_security_group_association" "this" {
  network_interface_id      = azurerm_network_interface.this.id
  network_security_group_id = azurerm_network_security_group.this.id
}

resource "azurerm_windows_virtual_machine" "this" {
  name                = var.name
  location            = var.location
  resource_group_name = var.resource_group_name
  size                = local.vm_size
  admin_username      = var.admin_username
  admin_password      = var.admin_password

  network_interface_ids = [azurerm_network_interface.this.id]

  # System-assigned managed identity: enables key-free access to private Azure Blob
  # storage when post_install_use_managed_identity is true. The principal_id output
  # can be used to assign Storage Blob Data Reader on the target container.
  identity {
    type = "SystemAssigned"
  }

  os_disk {
    caching              = "ReadWrite"
    storage_account_type = "StandardSSD_LRS"
  }

  source_image_reference {
    publisher = "MicrosoftWindowsServer"
    offer     = "WindowsServer"
    sku       = "2022-datacenter-azure-edition"
    version   = "latest"
  }

  tags = local.common_tags
}

# ---------------------------------------------------------------------------
# Optional domain join via the native Azure JsonADDomainExtension.
# The extension is only created when domain_name is non-empty.
# Password is passed via protected_settings so it is encrypted in transit
# and never exposed in the Azure portal extension view.
# ---------------------------------------------------------------------------

resource "azurerm_virtual_machine_extension" "domain_join" {
  count                      = local.domain_join_enabled ? 1 : 0
  name                       = "domain-join"
  virtual_machine_id         = azurerm_windows_virtual_machine.this.id
  publisher                  = "Microsoft.Compute"
  type                       = "JsonADDomainExtension"
  type_handler_version       = "1.3"
  auto_upgrade_minor_version = true

  # Settings are logged by Azure — no secrets here.
  settings = jsonencode({
    Name    = var.domain_name
    User    = var.domain_join_username
    OUPath  = var.domain_join_ou_path
    Restart = "true"
    Options = "3"
  })

  # Protected settings are encrypted by the Azure fabric and never shown in the portal.
  protected_settings = jsonencode({
    Password = var.domain_join_password
  })

  lifecycle {
    precondition {
      condition     = !local.domain_join_enabled || (trimspace(var.domain_join_username) != "" && trimspace(var.domain_join_password) != "")
      error_message = "domain_join_username and domain_join_password are required when domain_name is set."
    }
    precondition {
      condition     = !local.domain_join_enabled || length(var.dns_servers) > 0
      error_message = "dns_servers must contain at least one DC IP address when domain_name is set. The VM needs to resolve the domain controller before it can join the domain."
    }
  }

  tags = local.common_tags
}

# ---------------------------------------------------------------------------
# Optional post-build software installation via Chocolatey.
# Only created when chocolatey_packages is non-empty.
# Runs after domain join (if enabled) so the VM is fully domain-joined before
# any software is installed.
# ---------------------------------------------------------------------------

resource "azurerm_virtual_machine_extension" "post_install" {
  count                      = local.post_install_enabled ? 1 : 0
  name                       = "post-install"
  virtual_machine_id         = azurerm_windows_virtual_machine.this.id
  publisher                  = "Microsoft.Compute"
  type                       = "CustomScriptExtension"
  type_handler_version       = "1.10"
  auto_upgrade_minor_version = true

  # Three modes — see cse_settings local. Exit codes propagate through CSE to ARM,
  # surfacing failures on the VM's Extensions blade in the Azure portal.
  # No storage account key is used in any mode.
  settings = local.cse_settings

  # Ensure domain join completes first; safe to reference even when count = 0.
  depends_on = [azurerm_virtual_machine_extension.domain_join]

  tags = local.common_tags
}
