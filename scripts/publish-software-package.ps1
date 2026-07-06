param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,
    [Parameter(Mandatory = $true)]
    [string]$PackageId,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$StorageAccountName,
    [string]$ContainerName = 'packages',
    [ValidateSet('platform', 'customers')]
    [string]$Scope = 'platform',
    [string]$CustomerId
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ZipPath)) {
    throw "ZipPath not found: $ZipPath"
}

if ($Scope -eq 'customers' -and [string]::IsNullOrWhiteSpace($CustomerId)) {
    throw 'CustomerId is required when Scope is customers.'
}

if ($PackageId -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$') {
    throw 'PackageId must use lowercase letters/numbers with optional dot/hyphen separators.'
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Version must follow semver major.minor.patch (for example 24.09.0).'
}

$zipName = [IO.Path]::GetFileName($ZipPath)
$zipSha = (Get-FileHash -Algorithm SHA256 -Path $ZipPath).Hash.ToLower()

if ($Scope -eq 'platform') {
    $blobName = "catalog/platform/$PackageId/$Version/$zipName"
}
else {
    $blobName = "catalog/customers/$CustomerId/$PackageId/$Version/$zipName"
}

az storage blob upload `
    --account-name $StorageAccountName `
    --container-name $ContainerName `
    --name $blobName `
    --file $ZipPath `
    --auth-mode login `
    --overwrite true `
    --only-show-errors | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw 'Upload failed.'
}

Write-Host "Uploaded blob : $blobName"
Write-Host "ZIP SHA256    : $zipSha"
