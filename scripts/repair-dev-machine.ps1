param(
    [string]$CredentialClixmlPath,

    [Parameter(Mandatory = $true)]
    [string]$ServicePrincipalAppId,

    [Parameter(Mandatory = $true)]
    [string]$ServicePrincipalSecret,

    [Parameter(Mandatory = $true)]
    [string]$TenantId,

    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,

    [string]$KeyVaultName = 'azselfservice-1338-kv',

    [string]$KeyVaultUrl,

    [string]$EnvironmentPath = '.env',

    [string]$CustomerId,

    [string]$CustomerSecretRef = 'starting-secret',

    [switch]$SkipKeyVaultUpload,

    [switch]$SkipPlatformPatch
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)

    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Set-EnvValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $escapedName = [regex]::Escape($Name)
    $replacement = "${Name}=$Value"

    if (Test-Path -LiteralPath $Path) {
        $content = Get-Content -LiteralPath $Path -Raw
        if ($content -match "(?m)^$escapedName=.*$") {
            $content = [regex]::Replace($content, "(?m)^$escapedName=.*$", $replacement)
        }
        else {
            if (-not $content.EndsWith("`n")) {
                $content += "`n"
            }

            $content += "$replacement`n"
        }

        Set-Content -LiteralPath $Path -Value $content -NoNewline
        return
    }

    Set-Content -LiteralPath $Path -Value "$replacement`n"
}

function Get-RepoRoot {
    Split-Path -Parent $PSScriptRoot
}

function Invoke-PsqlScalar {
    param([string]$Sql)

    $result = docker exec azselfservice-postgres psql -U postgres -d azselfservice -t -A -c $Sql
    return $result.Trim()
}

function Assert-CommandAvailable {
    param([string]$Name, [string]$Message)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw $Message
    }
}

$repoRoot = Get-RepoRoot
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($CredentialClixmlPath)) {
    $CredentialClixmlPath = Join-Path $HOME 'CredentialStore/PROD-Automation.clixml'
}

if ([string]::IsNullOrWhiteSpace($KeyVaultUrl)) {
    $KeyVaultUrl = "https://$KeyVaultName.vault.azure.net/"
}

if (-not (Test-Path -LiteralPath $CredentialClixmlPath)) {
    $credentialDirectory = Split-Path -Parent $CredentialClixmlPath
    if (-not [string]::IsNullOrWhiteSpace($credentialDirectory) -and -not (Test-Path -LiteralPath $credentialDirectory)) {
        New-Item -ItemType Directory -Path $credentialDirectory -Force | Out-Null
    }
}

Write-Step 'Creating CLIXML credential file'
$secureSecret = ConvertTo-SecureString -String $ServicePrincipalSecret -AsPlainText -Force
$credential = [System.Management.Automation.PSCredential]::new($ServicePrincipalAppId, $secureSecret)
$credential | Export-Clixml -LiteralPath $CredentialClixmlPath

Write-Step 'Updating local .env values'
Set-EnvValue -Path $EnvironmentPath -Name 'AZURE_CLIENT_ID' -Value $ServicePrincipalAppId
Set-EnvValue -Path $EnvironmentPath -Name 'AZURE_CLIENT_SECRET' -Value $ServicePrincipalSecret
Set-EnvValue -Path $EnvironmentPath -Name 'AZURE_TENANT_ID' -Value $TenantId
Set-EnvValue -Path $EnvironmentPath -Name 'AZURE_SUBSCRIPTION_ID' -Value $SubscriptionId
Set-EnvValue -Path $EnvironmentPath -Name 'AZURE_KEYVAULT_NAME' -Value $KeyVaultName
Set-EnvValue -Path $EnvironmentPath -Name 'AZURE_KEYVAULT_URL' -Value $KeyVaultUrl

if (-not $SkipKeyVaultUpload) {
    Write-Step 'Uploading customer secret into Key Vault'
    Assert-CommandAvailable -Name 'az' -Message 'Azure CLI (az) is not installed or is not on PATH.'

    az login --service-principal --username $ServicePrincipalAppId --password=$ServicePrincipalSecret --tenant $TenantId --only-show-errors | Out-Null
    az keyvault secret set `
        --vault-name $KeyVaultName `
        --name $CustomerSecretRef `
        --value $ServicePrincipalSecret `
        --content-type "appid=$ServicePrincipalAppId" `
        --only-show-errors | Out-Null
}

if (-not $SkipPlatformPatch) {
    Write-Step 'Patching customer metadata and secret reference'
    Assert-CommandAvailable -Name 'docker' -Message 'Docker CLI was not found. Start Docker Desktop and retry.'

    if ([string]::IsNullOrWhiteSpace($CustomerId)) {
        $escapedSubscriptionId = $SubscriptionId.Replace("'", "''")
        $CustomerId = Invoke-PsqlScalar "select id from customers where subscription_id = '$escapedSubscriptionId' limit 1;"
    }

    if ([string]::IsNullOrWhiteSpace($CustomerId)) {
        throw "Could not resolve a customer row for subscription id '$SubscriptionId'."
    }

    $escapedCustomerId = $CustomerId.Replace("'", "''")
    $escapedTenantId = $TenantId.Replace("'", "''")
    $escapedSubscriptionId = $SubscriptionId.Replace("'", "''")
    $escapedCustomerSecretRef = $CustomerSecretRef.Replace("'", "''")

    docker exec azselfservice-postgres psql -U postgres -d azselfservice -c @"
update customers
set
    tenant_id = '$escapedTenantId',
    subscription_id = '$escapedSubscriptionId',
    sp_client_secret_secret_ref = '$escapedCustomerSecretRef',
    updated_at = current_timestamp
where id = '$escapedCustomerId';
"@ | Out-Null
}

Write-Host "`nRecovery complete." -ForegroundColor Green
Write-Host "CLIXML path        : $CredentialClixmlPath"
Write-Host "Environment file   : $EnvironmentPath"
Write-Host "Customer secret ref: $CustomerSecretRef"
Write-Host "Key Vault          : $KeyVaultName"
Write-Host "Tenant ID          : $TenantId"
Write-Host "Subscription ID    : $SubscriptionId"
if (-not [string]::IsNullOrWhiteSpace($CustomerId)) {
    Write-Host "Customer ID        : $CustomerId"
}