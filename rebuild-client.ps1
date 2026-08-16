# Stops any running client, rebuilds it, and reports honestly.
#
#   .\rebuild-client.ps1            build only
#   .\rebuild-client.ps1 -Launch    build then launch
#
# The client holds open handles on client\bin\Content.Client\*.dll while it runs, so a build
# started with it running fails with MSB3021/MSB3027 after burning a minute on retries. Stopping
# it first is not optional.
#
# Note the exit-code handling: piping dotnet's output through a filter makes $LASTEXITCODE reflect
# the filter, not the build. Capture to a file, check the code, then report.

param(
    [switch]$Launch,
    [string]$Configuration = "DebugOpt"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "client\Content.Client\Content.Client.csproj"
$log = Join-Path $env:TEMP "ss14-client-build.log"

$running = Get-Process | Where-Object { $_.MainWindowTitle -like "*Space Station 14*" }
foreach ($p in $running) {
    Write-Host "stopping client PID $($p.Id) to release bin\ locks" -ForegroundColor Yellow
    Stop-Process -Id $p.Id -Force
}
if ($running) { Start-Sleep -Seconds 2 }

Write-Host "building $Configuration..." -ForegroundColor Cyan
& dotnet build $proj -c $Configuration -v m *> $log
$code = $LASTEXITCODE

if ($code -ne 0) {
    Write-Host "BUILD FAILED (exit $code)" -ForegroundColor Red
    Select-String -Path $log -Pattern "error" | Select-Object -First 12 | ForEach-Object { $_.Line -replace '\[C:.*','' }
    exit $code
}

Write-Host "build OK" -ForegroundColor Green

if ($Launch) {
    & (Join-Path $root "run-client.ps1")
}
