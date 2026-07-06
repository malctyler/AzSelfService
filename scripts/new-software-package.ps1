param(
    [Parameter(Mandatory = $true)]
    [string]$PackageId,
    [Parameter(Mandatory = $true)]
    [string]$VendorSlug,
    [Parameter(Mandatory = $true)]
    [string]$ProductSlug,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [Parameter(Mandatory = $true)]
    [string]$DetectPath,
    [ValidateSet('msi', 'exe')]
    [string]$InstallerType = 'msi',
    [string]$SilentArgs,
    [switch]$SilentInstallArgsTested,
    [switch]$RebootSuppressionArgsTested,
    [string]$Architecture = 'x64',
    [string]$Os = 'windows',
    [string]$Publisher = 'Unknown Publisher',
    [string]$DisplayName,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\\software-packages')
)

$ErrorActionPreference = 'Stop'

function Assert-Slug {
    param(
        [string]$Value,
        [string]$Name
    )

    if ($Value -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
        throw "$Name must be lowercase letters/numbers/hyphens only."
    }
}

function Assert-SemVer {
    param([string]$Value)

    if ($Value -notmatch '^\d+\.\d+\.\d+$') {
        throw 'Version must follow semver major.minor.patch (for example 24.09.0).'
    }
}

function Test-LooksLikeHtml {
    param([byte[]]$Bytes)

    if ($null -eq $Bytes -or $Bytes.Length -eq 0) {
        return $false
    }

    $text = [System.Text.Encoding]::ASCII.GetString($Bytes).TrimStart()
    return $text.StartsWith('<!doctype', [System.StringComparison]::OrdinalIgnoreCase) -or
    $text.StartsWith('<html', [System.StringComparison]::OrdinalIgnoreCase) -or
    $text.StartsWith('<!DOCTYPE', [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-InstallerBinary {
    param(
        [string]$Path,
        [string]$Type
    )

    $fs = [System.IO.File]::OpenRead($Path)
    try {
        $header = New-Object byte[] 16
        $read = $fs.Read($header, 0, $header.Length)
        if ($read -le 0) {
            throw "Installer file is empty: $Path"
        }

        $probe = if ($read -lt $header.Length) { $header[0..($read - 1)] } else { $header }
        if (Test-LooksLikeHtml -Bytes $probe) {
            throw "Installer appears to be HTML, not a binary file: $Path"
        }

        if ($Type -eq 'msi') {
            $isMsi = $read -ge 8 -and
            $header[0] -eq 0xD0 -and $header[1] -eq 0xCF -and $header[2] -eq 0x11 -and $header[3] -eq 0xE0 -and
            $header[4] -eq 0xA1 -and $header[5] -eq 0xB1 -and $header[6] -eq 0x1A -and $header[7] -eq 0xE1
            if (-not $isMsi) {
                throw "Installer does not have a valid MSI header: $Path"
            }
        }

        if ($Type -eq 'exe') {
            $isExe = $read -ge 2 -and $header[0] -eq 0x4D -and $header[1] -eq 0x5A
            if (-not $isExe) {
                throw "Installer does not have a valid EXE header: $Path"
            }
        }
    }
    finally {
        $fs.Dispose()
    }
}

Assert-Slug -Value $VendorSlug -Name 'VendorSlug'
Assert-Slug -Value $ProductSlug -Name 'ProductSlug'
Assert-SemVer -Value $Version

if ($PackageId -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*$') {
    throw 'PackageId must use lowercase letters/numbers with optional dot/hyphen separators.'
}

if (-not (Test-Path -LiteralPath $InstallerPath)) {
    throw "InstallerPath not found: $InstallerPath"
}

Assert-InstallerBinary -Path $InstallerPath -Type $InstallerType

if (-not $SilentInstallArgsTested) {
    throw 'Set -SilentInstallArgsTested after confirming the package args run unattended.'
}

if (-not $RebootSuppressionArgsTested) {
    throw 'Set -RebootSuppressionArgsTested after confirming args suppress reboot for platform-managed reboot sequencing.'
}

if ([string]::IsNullOrWhiteSpace($DisplayName)) {
    $DisplayName = $ProductSlug
}

$folderName = "$VendorSlug-$ProductSlug-$Version-$Os-$Architecture-$InstallerType"
$workDir = Join-Path $OutputRoot $folderName
$payloadDir = Join-Path $workDir 'payload'
$scriptsDir = Join-Path $workDir 'scripts'

New-Item -ItemType Directory -Force -Path $payloadDir, $scriptsDir | Out-Null

$installerFileName = [IO.Path]::GetFileName($InstallerPath)
$installerDestination = Join-Path $payloadDir $installerFileName
Copy-Item -LiteralPath $InstallerPath -Destination $installerDestination -Force

$installerSha = (Get-FileHash -Algorithm SHA256 -Path $installerDestination).Hash.ToLower()

if ($InstallerType -eq 'msi') {
    $installCommand = 'msiexec.exe'
    if ([string]::IsNullOrWhiteSpace($SilentArgs)) {
        $SilentArgs = "/i payload\\$installerFileName /qn /norestart"
    }
    $scriptArgs = "/i `"`$installerPath`" /qn /norestart"
}
else {
    $installCommand = "payload/$installerFileName"
    if ([string]::IsNullOrWhiteSpace($SilentArgs)) {
        $SilentArgs = '/quiet /norestart'
    }
    $scriptArgs = '/quiet /norestart'
}

$manifest = [ordered]@{
    packageId         = $PackageId
    displayName       = $DisplayName
    version           = $Version
    publisher         = $Publisher
    os                = $Os
    architecture      = $Architecture
    installerType     = $InstallerType
    entrypoint        = 'scripts/install.ps1'
    installCommand    = $installCommand
    silentArgs        = $SilentArgs
    silentInstallArgsTested = $true
    rebootSuppressionArgsTested = $true
    expectedExitCodes = @(0, 3010)
    rebootBehavior    = 'possible'
    detectionRules    = @(
        [ordered]@{
            type = 'fileExists'
            path = $DetectPath
        }
    )
    artifacts         = @(
        [ordered]@{
            path   = "payload/$installerFileName"
            sha256 = $installerSha
        }
    )
}

$manifestJson = $manifest | ConvertTo-Json -Depth 6
Set-Content -Path (Join-Path $workDir 'manifest.json') -Value $manifestJson -NoNewline

$installScript = if ($InstallerType -eq 'msi') {
    @"
`$ErrorActionPreference = 'Stop'
`$scriptDir = Split-Path -Parent `$MyInvocation.MyCommand.Path
`$root = Resolve-Path (Join-Path `$scriptDir '..')
`$installerPath = Join-Path `$root 'payload\\$installerFileName'

if (-not (Test-Path -LiteralPath `$installerPath)) {
    throw "Installer not found at `$installerPath"
}

`$process = Start-Process -FilePath 'msiexec.exe' -ArgumentList "/i `"`$installerPath`" /qn /norestart" -PassThru -Wait
if (`$process.ExitCode -notin @(0, 3010)) {
    throw "Install failed with exit code `$(`$process.ExitCode)"
}

exit `$process.ExitCode
"@
}
else {
    @"
`$ErrorActionPreference = 'Stop'
`$scriptDir = Split-Path -Parent `$MyInvocation.MyCommand.Path
`$root = Resolve-Path (Join-Path `$scriptDir '..')
`$installerPath = Join-Path `$root 'payload\\$installerFileName'

if (-not (Test-Path -LiteralPath `$installerPath)) {
    throw "Installer not found at `$installerPath"
}

`$process = Start-Process -FilePath `$installerPath -ArgumentList '/quiet /norestart' -PassThru -Wait
if (`$process.ExitCode -notin @(0, 3010)) {
    throw "Install failed with exit code `$(`$process.ExitCode)"
}

exit `$process.ExitCode
"@
}
Set-Content -Path (Join-Path $scriptsDir 'install.ps1') -Value $installScript -NoNewline

$detectScript = @"
`$detectPath = '$($DetectPath.Replace("'", "''"))'
if (Test-Path -LiteralPath `$detectPath) {
    Write-Host 'Detected package.'
    exit 0
}

Write-Host "Package not detected at `$detectPath"
exit 1
"@
Set-Content -Path (Join-Path $scriptsDir 'detect.ps1') -Value $detectScript -NoNewline

Set-Content -Path (Join-Path $workDir 'checksums.sha256') -Value "$installerSha  payload/$installerFileName" -NoNewline

$zipPath = Join-Path $OutputRoot "$folderName.zip"
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $workDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zipSha = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLower()

Write-Host "Package folder: $workDir"
Write-Host "Package zip   : $zipPath"
Write-Host "ZIP SHA256    : $zipSha"
