$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsPath = Join-Path ([System.IO.Path]::GetTempPath()) ("adventureworks-validate-" + [guid]::NewGuid().ToString('N'))

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Command
    )

    Write-Host "`n== $Name =="
    & $Command
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode."
    }
}

Push-Location $repoRoot
try {
    Invoke-Checked 'dotnet build' { dotnet build AdventureWorks.slnx --artifacts-path $artifactsPath }
    Invoke-Checked 'dotnet test' { dotnet test AdventureWorks.slnx --artifacts-path $artifactsPath }
    Invoke-Checked 'npm run test' { npm.cmd run test --prefix src/client }
    Invoke-Checked 'npm run lint' { npm.cmd run lint --prefix src/client }
    Invoke-Checked 'npm run build' { npm.cmd run build --prefix src/client }
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $artifactsPath) {
        Remove-Item -LiteralPath $artifactsPath -Recurse -Force
    }
}