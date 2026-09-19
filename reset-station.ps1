# Cycles the whole dev environment: server, client, and any stray copilot connections.
#
#   .\reset-station.ps1
#
# Both halves must restart together. Killing the server alone leaves the client rendering a
# stale world - it has no idea the peer is gone until Lidgren times out, which looks exactly
# like the reset silently failing. And a client killed uncleanly wedges the bridge's accept
# loop, which serves one connection inline, so the next connection never gets served.

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "stopping everything..." -ForegroundColor Yellow

Get-Process node -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process | Where-Object { $_.MainWindowTitle -like "*Space Station 14*" } | Stop-Process -Force
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -match 'Content\.Server' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

Start-Sleep -Seconds 3

Write-Host "starting server..." -ForegroundColor Cyan
$log = Join-Path $root "logs\server.log"
Start-Process -FilePath "dotnet" -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError (Join-Path $root "logs\server.err.log") -ArgumentList @(
    (Join-Path $root "client\bin\Content.Server\Content.Server.dll"),
    "--config-file", (Join-Path $root "server\server_config.toml"),
    "--data-dir",    (Join-Path $root "server\data")
)

# Wait for the station to finish loading rather than guessing at a sleep duration.
$deadline = (Get-Date).AddMinutes(3)
while ((Get-Date) -lt $deadline) {
    if ((Test-Path $log) -and (Select-String -Path $log -Pattern "Socket bound to 127.0.0.1:1212" -Quiet)) { break }
    Start-Sleep -Seconds 3
}

$station = (Select-String -Path $log -Pattern "Set up station (.+) \(" | Select-Object -Last 1).Matches.Groups[1].Value
Write-Host "server ready: $station" -ForegroundColor Green

Write-Host "starting client..." -ForegroundColor Cyan
$env:ROBUST_DISABLE_SANDBOX = "1"
Start-Process -FilePath "dotnet" -RedirectStandardOutput (Join-Path $root "logs\client.log") -RedirectStandardError (Join-Path $root "logs\client.err.log") -ArgumentList @(
    (Join-Path $root "client\bin\Content.Client\Content.Client.dll"),
    "--connect", "--connect-address", "udp://127.0.0.1:1212", "--username", "Copilot"
)

# The daemon holds the copilot's connection open. Without it the panel reads "offline" and
# anything the human types gets "No copilot connected" - which is exactly what the reset was
# meant to avoid. Started last so the client is already coming up; the daemon retries until the
# bridge answers.
Write-Host "starting copilot daemon..." -ForegroundColor Cyan
Start-Process -FilePath "node" -WindowStyle Hidden -WorkingDirectory (Join-Path $root "mcp") -ArgumentList "daemon.mjs"

Write-Host "done - client is loading, copilot will attach automatically" -ForegroundColor Green
