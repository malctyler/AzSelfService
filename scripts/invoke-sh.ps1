param(
    [Parameter(Mandatory = $true)]
    [string]$ScriptRelativePath
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$scriptFullPath = Join-Path $repoRoot $ScriptRelativePath

if (-not (Test-Path $scriptFullPath)) {
    Write-Error "Script not found: $scriptFullPath"
    exit 1
}

$wslPath = "C:\Program Files\WSL\wsl.exe"
$gitBashPath = "C:\Program Files\Git\bin\bash.exe"

function Convert-WindowsPathToWsl {
    param([string]$Path)

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $normalized = $resolved -replace '\\', '/'
    if ($normalized -match '^([A-Za-z]):/(.*)$') {
        $drive = $Matches[1].ToLowerInvariant()
        $rest = $Matches[2]
        return "/mnt/$drive/$rest"
    }

    throw "Unable to convert path to WSL format: $resolved"
}

function Convert-WindowsPathToGitBash {
    param([string]$Path)

    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $normalized = $resolved -replace '\\', '/'
    if ($normalized -match '^([A-Za-z]):/(.*)$') {
        $drive = $Matches[1].ToLowerInvariant()
        $rest = $Matches[2]
        return "/$drive/$rest"
    }

    throw "Unable to convert path to Git Bash format: $resolved"
}

if (Test-Path $wslPath) {
    Write-Host "Running via WSL: $ScriptRelativePath"

    $linuxRepo = Convert-WindowsPathToWsl -Path $repoRoot

    $linuxScript = "$linuxRepo/$ScriptRelativePath".Replace("\\", "/")
    & $wslPath sh -lc "cd '$linuxRepo' && chmod +x '$linuxScript' && '$linuxScript'"
    if ($LASTEXITCODE -eq 0) {
        exit 0
    }

    Write-Warning "WSL execution failed with exit code $LASTEXITCODE. Trying Git Bash fallback..."
}

if (Test-Path $gitBashPath) {
    Write-Host "Running via Git Bash: $ScriptRelativePath"

    $gitBashRepo = Convert-WindowsPathToGitBash -Path $repoRoot
    $gitBashScript = "$gitBashRepo/$ScriptRelativePath"

    & $gitBashPath -lc "cd '$gitBashRepo' && chmod +x '$gitBashScript' && '$gitBashScript'"
    exit $LASTEXITCODE
}

Write-Error "Neither WSL nor Git Bash was found. Install one of them or run Docker commands directly from PowerShell."
exit 1
