$ErrorActionPreference = 'Stop'

function Get-ComposeCommand {
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        try {
            docker compose version | Out-Null
            if ($LASTEXITCODE -eq 0) {
                return @('docker', 'compose')
            }
        }
        catch {
            # Fall through and try docker-compose.
        }
    }

    if (Get-Command docker-compose -ErrorAction SilentlyContinue) {
        return @('docker-compose')
    }

    return $null
}

function Invoke-Compose {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ComposeCommand,
        [Parameter(Mandatory = $true)]
        [string[]]$Args
    )

    if ($ComposeCommand.Count -eq 2) {
        & $ComposeCommand[0] $ComposeCommand[1] @Args
    }
    else {
        & $ComposeCommand[0] @Args
    }

    if ($LASTEXITCODE -ne 0) {
        throw "Compose command failed: $($ComposeCommand -join ' ') $($Args -join ' ')"
    }
}

Write-Host "Restarting AzSelfService Development Environment"
Write-Host "==================================================="
Write-Host ""

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Error "Docker CLI was not found. Install Docker Desktop: https://www.docker.com/products/docker-desktop"
    exit 1
}

docker info | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker Desktop is not running or the daemon is unreachable. Start Docker Desktop and retry."
    exit 1
}

$composeCommand = Get-ComposeCommand
if (-not $composeCommand) {
    Write-Error "Neither 'docker compose' nor 'docker-compose' is available. Ensure Docker Desktop is installed and updated."
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "Stopping services..."
Write-Host ""

Invoke-Compose -ComposeCommand $composeCommand -Args @('--profile', 'dev', 'down', '--remove-orphans')

Write-Host ""
Write-Host "Services stopped. Starting up again..."
Write-Host ""

$envFile = '.env'
if (-not (Test-Path $envFile)) {
    if (Test-Path '.env.docker') {
        $envFile = '.env.docker'
    }
    else {
        Write-Error "Neither .env nor .env.docker was found. Run .\scripts\dev-setup.ps1 first."
        exit 1
    }
}

Write-Host "Loaded environment from $envFile"
Write-Host "Starting Docker Compose services..."

Invoke-Compose -ComposeCommand $composeCommand -Args @('--profile', 'dev', 'up', '-d', '--build')

Write-Host ""
Write-Host "Services restarted."
Write-Host ""
Write-Host "Access Points:"
Write-Host "  Frontend:     http://localhost:3000"
Write-Host "  Backend API:  http://localhost:5000"
Write-Host "  API Docs:     http://localhost:5000/swagger"
Write-Host "  PostgreSQL:   localhost:5432"
Write-Host ""
Write-Host "Useful Commands:"
if ($composeCommand.Count -eq 2) {
    Write-Host "  Logs:         docker compose --profile dev logs -f"
    Write-Host "  Stop:         .\scripts\dev-down.ps1"
    Write-Host "  Restart:      .\scripts\dev-restart.ps1"
}
else {
    Write-Host "  Logs:         docker-compose --profile dev logs -f"
    Write-Host "  Stop:         .\scripts\dev-down.ps1"
    Write-Host "  Restart:      .\scripts\dev-restart.ps1"
}
