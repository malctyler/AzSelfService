#!/usr/bin/env pwsh
# Test script to measure Azure storage account name availability check timing

# You'll need to set these variables:
# - subscriptionId: Your Azure subscription ID
# - tenantId: Your Azure tenant ID  
# - clientId: Service principal client ID
# - clientSecret: Service principal secret

param(
    [string]$subscriptionId = "YOUR_SUBSCRIPTION_ID",
    [string]$tenantId = "YOUR_TENANT_ID", 
    [string]$clientId = "YOUR_CLIENT_ID",
    [string]$clientSecret = "YOUR_CLIENT_SECRET",
    [string[]]$namesToTest = @("teststorageac", "myuniquename12345")
)

function Test-StorageNameAvailability {
    param(
        [string]$Name,
        [string]$SubscriptionId,
        [string]$TenantId,
        [string]$ClientId,
        [string]$ClientSecret
    )

    $totalStart = [DateTime]::UtcNow
    
    # Step 1: Get token
    $tokenStart = [DateTime]::UtcNow
    $tokenUrl = "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token"
    $body = @{
        grant_type    = "client_credentials"
        client_id     = $ClientId
        client_secret = $ClientSecret
        scope         = "https://management.azure.com/.default"
    }
    
    $tokenResponse = Invoke-RestMethod -Method Post -Uri $tokenUrl -Body $body
    $token = $tokenResponse.access_token
    $tokenElapsed = ([DateTime]::UtcNow - $tokenStart).TotalMilliseconds
    Write-Host "[TIMING] Token acquisition: ${tokenElapsed}ms"
    
    # Step 2: Check name availability
    $checkStart = [DateTime]::UtcNow
    $checkUrl = "https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.Storage/checkNameAvailability?api-version=2023-01-01"
    $headers = @{
        Authorization = "Bearer $token"
        "Content-Type" = "application/json"
    }
    $checkBody = @{
        name = $Name
        type = "Microsoft.Storage/storageAccounts"
    } | ConvertTo-Json
    
    try {
        $checkResponse = Invoke-RestMethod -Method Post -Uri $checkUrl -Headers $headers -Body $checkBody
        $checkElapsed = ([DateTime]::UtcNow - $checkStart).TotalMilliseconds
        Write-Host "[TIMING] Azure checkNameAvailability API call: ${checkElapsed}ms"
        Write-Host "[TIMING] Total time for '$Name': $([DateTime]::UtcNow - $totalStart | Select-Object -ExpandProperty TotalMilliseconds)ms"
        Write-Host "Result for '$Name': Available=$($checkResponse.nameAvailable), Message=$($checkResponse.message)"
    }
    catch {
        $checkElapsed = ([DateTime]::UtcNow - $checkStart).TotalMilliseconds
        Write-Host "[TIMING] Azure checkNameAvailability API call (failed): ${checkElapsed}ms"
        Write-Host "[TIMING] Total time for '$Name': $([DateTime]::UtcNow - $totalStart | Select-Object -ExpandProperty TotalMilliseconds)ms"
        Write-Host "Error for '$Name': $_"
    }
}

Write-Host "Testing Azure Storage Account Name Availability timing"
Write-Host "======================================================="

foreach ($name in $namesToTest) {
    Write-Host "`nTesting name: $name"
    Test-StorageNameAvailability -Name $name -SubscriptionId $subscriptionId -TenantId $tenantId -ClientId $clientId -ClientSecret $clientSecret
    Write-Host "---"
}
