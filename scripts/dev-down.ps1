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

Write-Host "Stopping AzSelfService Development Environment"
Write-Host "=================================================="
Write-Host ""

$composeCommand = Get-ComposeCommand
if (-not $composeCommand) {
	Write-Error "Neither 'docker compose' nor 'docker-compose' is available. Ensure Docker Desktop is installed and updated."
	exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Invoke-Compose -ComposeCommand $composeCommand -Args @('--profile', 'dev', 'down', '--remove-orphans')

Write-Host ""
Write-Host "Services stopped."
Write-Host ""
Write-Host "To remove volumes as well:"
if ($composeCommand.Count -eq 2) {
	Write-Host "  docker compose --profile dev down -v"
}
else {
	Write-Host "  docker-compose --profile dev down -v"
}
