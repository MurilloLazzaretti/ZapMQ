# Builds the panel interface and then the service with it inside.
# The result is in publish\win-x64: ZapMQ.exe and appsettings.json.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Push-Location src\ZapMQ.Panel
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    npm run build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

if (Test-Path publish\win-x64) { Remove-Item publish\win-x64 -Recurse -Force }
dotnet publish src\ZapMQ.Server -c Release -r win-x64 -o publish\win-x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'Published to publish\win-x64'
