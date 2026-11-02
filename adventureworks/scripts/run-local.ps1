param(
	[Parameter(Mandatory = $true)]
	[ValidateSet('Api', 'Client', 'Verify')]
	[string]$Mode,
	[ValidateRange(1, 65535)]
	[int]$ApiPort = 5080,
	[ValidateRange(1, 65535)]
	[int]$ClientPort = 5184
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$clientDirectory = Join-Path $repositoryRoot 'src/client'
$apiProject = Join-Path $repositoryRoot 'src/server/AdventureWorks.Api'
$apiUrl = "http://localhost:$ApiPort"
$viteUrl = "http://127.0.0.1:$ClientPort"
$apiListenUrl = $apiUrl
$apiHealthUrl = "$apiUrl/api/health"

if ($Mode -eq 'Client' -and $ApiPort -eq $ClientPort) {
	throw 'ApiPort and ClientPort must be different. No process was started or stopped.'
}

function Get-ListeningConnections {
	param([int]$Port)

	try {
		$listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop)
		return @($listeners | Where-Object { $_.LocalPort -eq $Port })
	}
	catch {
		throw "Could not safely inspect TCP port $Port. No process was started or stopped. $($_.Exception.Message)"
	}
}

function Assert-PortAvailable {
	param([int]$Port)

	$listeners = Get-ListeningConnections -Port $Port
	if ($listeners.Count -eq 0) {
		return
	}

	$owners = foreach ($listener in $listeners) {
		$process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
		$name = if ($process) { $process.ProcessName } else { 'unknown process' }
		"$($listener.LocalAddress) (PID $($listener.OwningProcess), $name)"
	}

	throw "Port $Port is already listening: $($owners -join '; '). Nothing was started or stopped. Inspect ownership yourself, or choose another free port with the corresponding -ApiPort or -ClientPort argument."
}

function Assert-LoopbackListener {
	param([int]$Port)

	$listeners = Get-ListeningConnections -Port $Port
	if ($listeners.Count -eq 0) {
		throw "Nothing is listening on required port $Port. Start the loopback API before verification."
	}

	$nonLoopback = @($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1', '::ffff:127.0.0.1') })
	if ($nonLoopback.Count -gt 0) {
		throw "Port $Port has a non-loopback listener. Verification stopped without sending HTTP requests."
	}
}

function Assert-HealthResponse {
	param([string]$Url)

	try {
		$response = Invoke-WebRequest -Uri $Url -Method Get -TimeoutSec 5 -UseBasicParsing
		$body = $response.Content | ConvertFrom-Json
		if ($response.StatusCode -ne 200 -or $body.status -ne 'ok') {
			throw "Expected HTTP 200 with status 'ok'; received HTTP $($response.StatusCode)."
		}
		Write-Host "PASS $Url (HTTP $($response.StatusCode), status=$($body.status))"
	}
	catch {
		throw "Health check failed for $Url. $($_.Exception.Message)"
	}
}

function Assert-AuthenticatedUiResponse {
	param([string]$Url)

	try {
		$response = Invoke-WebRequest -Uri $Url -Method Get -TimeoutSec 10 -UseBasicParsing -UseDefaultCredentials
		$contentType = [string]$response.Headers['Content-Type']
		if ($response.StatusCode -ne 200 -or $contentType -notmatch '^text/html\b' -or $response.Content -notmatch '(?is)<html\b') {
			throw "Expected authenticated HTTP 200 HTML; received HTTP $($response.StatusCode) with content type '$contentType'."
		}
		Write-Host "PASS authenticated UI $Url (HTTP $($response.StatusCode), Windows default credentials)"
	}
	catch {
		throw "Authenticated UI check failed for $Url using Windows default credentials. $($_.Exception.Message)"
	}
}

switch ($Mode) {
	'Api' {
		Assert-PortAvailable -Port $ApiPort
		Write-Host "Authenticated UI URL (open this on localhost): $apiUrl"
		Write-Host "API health: $apiHealthUrl"
		Write-Host "Building React client into the API wwwroot before launch..."
		& npm.cmd run build --prefix $clientDirectory
		if ($LASTEXITCODE -ne 0) {
			throw "Client build failed with exit code $LASTEXITCODE. The API was not started."
		}

		$environmentNames = @('ASPNETCORE_ENVIRONMENT', 'ADVENTUREWORKS_DEV_AUTH_ENABLED')
		$previousEnvironment = @{}
		$artifactsPath = Join-Path ([System.IO.Path]::GetTempPath()) ("adventureworks-local-api-" + [guid]::NewGuid().ToString('N'))
		foreach ($name in $environmentNames) {
			$previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
		}
		try {
			$env:ASPNETCORE_ENVIRONMENT = 'Development'
			$env:ADVENTUREWORKS_DEV_AUTH_ENABLED = 'true'
			Push-Location $repositoryRoot
			try {
				& dotnet run --project $apiProject --no-launch-profile --artifacts-path $artifactsPath --urls $apiListenUrl
			}
			finally {
				Pop-Location
			}
		}
		finally {
			foreach ($name in $environmentNames) {
				[Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
			}
			if (Test-Path -LiteralPath $artifactsPath) {
				Remove-Item -LiteralPath $artifactsPath -Recurse -Force
			}
		}
	}
	'Client' {
		Assert-PortAvailable -Port $ClientPort
		Write-Warning 'Vite is an unauthenticated UI-only preview. It has no /api proxy and is not the authenticated API workflow.'
		Write-Host "Vite preview URL (not authenticated API access): $viteUrl"
		& npm.cmd run dev --prefix $clientDirectory -- --host 127.0.0.1 --port $ClientPort --strictPort
	}
	'Verify' {
		Assert-LoopbackListener -Port $ApiPort
		Assert-HealthResponse -Url $apiHealthUrl
		Assert-AuthenticatedUiResponse -Url $apiUrl
	}
}