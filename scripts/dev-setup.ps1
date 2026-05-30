$ErrorActionPreference = 'Stop'

Write-Host "AzSelfService Development Environment Setup"
Write-Host "=============================================="
Write-Host ""

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Test-Path '.env')) {
	if (Test-Path '.env.docker') {
		Copy-Item '.env.docker' '.env'
		Write-Host "Created .env from .env.docker"
	}
	elseif (Test-Path '.env.example') {
		Copy-Item '.env.example' '.env'
		Write-Host "Created .env from .env.example"
	}
	else {
		Write-Error "Could not find .env.docker or .env.example to bootstrap .env"
		exit 1
	}
}
else {
	Write-Host ".env already exists"
}

if (-not (Test-Path 'logs')) {
	New-Item -ItemType Directory -Path 'logs' | Out-Null
	Write-Host "Created logs directory"
}
else {
	Write-Host "logs directory already exists"
}

Write-Host ""
Write-Host "Setup complete."
Write-Host "Next: run .\scripts\dev-up.ps1"
