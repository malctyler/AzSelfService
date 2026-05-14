param(
    [string]$CredentialClixmlPath,
    [string]$ServicePrincipalAppId,
    [string]$ServicePrincipalSecret,
    [Parameter(Mandatory = $true)]
    [string]$TenantId,
    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,
    [Parameter(Mandatory = $true)]
    [string]$KeyVaultName,
    [Parameter(Mandatory = $true)]
    [string]$StorageAccountName,
    [string]$TfStateContainerName = 'customer-tfstate',
    [string]$Location = 'uksouth',
    [string]$SecretName = 'starting-secret',
    [string]$SecretValue,
    [switch]$SkipLogin
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Invoke-AzRoleAssignmentWithRetry {
    param(
        [string]$PrincipalAppId,
        [string]$Scope,
        [string]$Role = 'Key Vault Administrator',
        [int]$MaxAttempts = 6
    )

    # Get object ID from app ID
    $objectId = az ad sp show --id $PrincipalAppId --query id -o tsv
    if ([string]::IsNullOrWhiteSpace($objectId)) {
        throw "Could not resolve object ID for service principal app ID: $PrincipalAppId"
    }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            $existing = az role assignment list `
                --assignee-object-id $objectId `
                --scope $Scope `
                --role $Role `
                --query '[0].id' `
                -o tsv 2>$null

            if (-not [string]::IsNullOrWhiteSpace($existing)) {
                return
            }

            az role assignment create `
                --assignee-object-id $objectId `
                --assignee-principal-type ServicePrincipal `
                --role $Role `
                --scope $Scope `
                --only-show-errors | Out-Null

            return
        }
        catch {
            if ($attempt -eq $MaxAttempts) {
                throw
            }

            Start-Sleep -Seconds ([Math]::Min(15, [Math]::Pow(2, $attempt)))
        }
    }
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI (az) is not installed or is not on PATH.'
}

if ($CredentialClixmlPath) {
    if (-not (Test-Path -LiteralPath $CredentialClixmlPath)) {
        throw "Credential CLIXML file not found: $CredentialClixmlPath"
    }

    $creds = Import-Clixml -LiteralPath $CredentialClixmlPath
    $networkCredential = $creds.GetNetworkCredential()
    $ServicePrincipalAppId = $networkCredential.UserName
    $ServicePrincipalSecret = $networkCredential.Password
}

if ([string]::IsNullOrWhiteSpace($ServicePrincipalAppId)) {
    throw 'ServicePrincipalAppId is required, or provide CredentialClixmlPath.'
}

if ([string]::IsNullOrWhiteSpace($ServicePrincipalSecret)) {
    throw 'ServicePrincipalSecret is required, or provide CredentialClixmlPath.'
}

if ([string]::IsNullOrWhiteSpace($SecretValue)) {
    $SecretValue = $ServicePrincipalSecret
}

Write-Step 'Signing in with service principal'
if (-not $SkipLogin) {
    az login --service-principal --username $ServicePrincipalAppId --password $ServicePrincipalSecret --tenant $TenantId --only-show-errors | Out-Null
}

Write-Step "Selecting subscription $SubscriptionId"
az account set --subscription $SubscriptionId --only-show-errors | Out-Null

Write-Step "Creating resource group $ResourceGroupName in $Location"
az group create `
    --name $ResourceGroupName `
    --location $Location `
    --tags managed-by=azselfservice purpose=bootstrap `
    --only-show-errors | Out-Null

Write-Step "Creating Key Vault $KeyVaultName with RBAC authorization"
az keyvault create `
    --name $KeyVaultName `
    --resource-group $ResourceGroupName `
    --location $Location `
    --enable-rbac-authorization true `
    --sku standard `
    --only-show-errors | Out-Null

$vaultId = az keyvault show `
    --name $KeyVaultName `
    --resource-group $ResourceGroupName `
    --query id `
    -o tsv

Write-Step 'Assigning Key Vault Administrator role to the service principal'
Invoke-AzRoleAssignmentWithRetry -PrincipalAppId $ServicePrincipalAppId -Scope $vaultId

Write-Step "Creating storage account $StorageAccountName"
az storage account create `
    --name $StorageAccountName `
    --resource-group $ResourceGroupName `
    --location $Location `
    --sku Standard_LRS `
    --kind StorageV2 `
    --allow-blob-public-access false `
    --min-tls-version TLS1_2 `
    --tags managed-by=azselfservice purpose=tfstate `
    --only-show-errors | Out-Null

$storageId = az storage account show `
    --name $StorageAccountName `
    --resource-group $ResourceGroupName `
    --query id `
    -o tsv

Write-Step "Assigning Storage Blob Data Owner role to the service principal"
Invoke-AzRoleAssignmentWithRetry `
    -PrincipalAppId $ServicePrincipalAppId `
    -Scope $storageId `
    -Role 'Storage Blob Data Owner'

Write-Step "Creating blob container $TfStateContainerName"
# Retry with back-off until the RBAC assignment propagates and the SP can create the container
for ($attempt = 1; $attempt -le 10; $attempt++) {
    try {
        az storage container create `
            --name $TfStateContainerName `
            --account-name $StorageAccountName `
            --auth-mode login `
            --only-show-errors | Out-Null
        Write-Host "Container '$TfStateContainerName' created." -ForegroundColor Green
        break
    }
    catch {
        if ($attempt -eq 10) {
            throw
        }

        $waitTime = [Math]::Min(30, [Math]::Pow(2, $attempt - 1))
        Write-Host "Attempt $attempt failed (RBAC may still be propagating), retrying in ${waitTime}s..." -ForegroundColor Yellow
        Start-Sleep -Seconds $waitTime
    }
}

Write-Step "Uploading secret $SecretName"
# RBAC propagation can take time, wait before attempting
Start-Sleep -Seconds 5

for ($attempt = 1; $attempt -le 10; $attempt++) {
    try {
        az keyvault secret set `
            --vault-name $KeyVaultName `
            --name $SecretName `
            --value $SecretValue `
            --content-type "appid=$ServicePrincipalAppId" `
            --only-show-errors | Out-Null
        Write-Host "Secret uploaded successfully." -ForegroundColor Green
        break
    }
    catch {
        if ($attempt -eq 10) {
            throw
        }

        $waitTime = [Math]::Min(30, [Math]::Pow(2, $attempt - 1))
        Write-Host "Attempt $attempt failed, retrying in ${waitTime}s..." -ForegroundColor Yellow
        Start-Sleep -Seconds $waitTime
    }
}

Write-Host "`nBootstrap complete." -ForegroundColor Green
Write-Host "Resource group     : $ResourceGroupName"
Write-Host "Key Vault          : $KeyVaultName"
Write-Host "Storage account    : $StorageAccountName"
Write-Host "TF state container : $TfStateContainerName"
Write-Host "Secret name        : $SecretName"
Write-Host "Subscription       : $SubscriptionId"
Write-Host "Tenant             : $TenantId"
