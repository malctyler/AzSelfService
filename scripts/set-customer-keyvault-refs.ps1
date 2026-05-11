param(
    [string]$CustomerId,
    [string]$SubscriptionId,
    [Parameter(Mandatory = $true)]
    [string]$KeyVaultName,
    [string]$SecretPrefix,
    [string]$PostgresContainerName = 'azselfservice-postgres',
    [string]$DatabaseName = 'azselfservice',
    [string]$DatabaseUser = 'postgres',
    [switch]$UseSecretUris
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Invoke-PsqlScalar {
    param([string]$Sql)

    $result = docker exec $PostgresContainerName psql -U $DatabaseUser -d $DatabaseName -t -A -c $Sql
    return $result.Trim()
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is not installed or is not on PATH.'
}

if ([string]::IsNullOrWhiteSpace($CustomerId) -and [string]::IsNullOrWhiteSpace($SubscriptionId)) {
    throw 'Provide either CustomerId or SubscriptionId.'
}

if ([string]::IsNullOrWhiteSpace($CustomerId)) {
    Write-Step "Resolving customer id for subscription $SubscriptionId"
    $escapedSubscriptionId = $SubscriptionId.Replace("'", "''")
    $CustomerId = Invoke-PsqlScalar "select id from customers where subscription_id = '$escapedSubscriptionId' limit 1;"

    if ([string]::IsNullOrWhiteSpace($CustomerId)) {
        throw "No customer found for subscription id '$SubscriptionId'."
    }
}

if ([string]::IsNullOrWhiteSpace($SecretPrefix)) {
    $SecretPrefix = "customers/$CustomerId"
}

$clientSecretRef = "$SecretPrefix/sp-client-secret"

if ($UseSecretUris) {
    $clientSecretRef = "https://$KeyVaultName.vault.azure.net/secrets/$clientSecretRef"
}

$escapedCustomerId = $CustomerId.Replace("'", "''")
$escapedClientSecretRef = $clientSecretRef.Replace("'", "''")

Write-Step "Updating Key Vault secret references for customer $CustomerId"
docker exec $PostgresContainerName psql -U $DatabaseUser -d $DatabaseName -c @"
update customers
set
    sp_client_secret_secret_ref = '$escapedClientSecretRef',
    updated_at = current_timestamp
where id = '$escapedCustomerId';
"@ | Out-Null

Write-Host "`nCustomer secret references updated." -ForegroundColor Green
Write-Host "Customer ID                  : $CustomerId"
Write-Host "Client secret secret ref     : $clientSecretRef"
Write-Host "Tenant/subscription source   : customers.tenant_id / customers.subscription_id"