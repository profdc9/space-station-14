# Launches the copilot-modified SS14 client against the local dev server.
#
#   .\run-client.ps1
#
# ROBUST_DISABLE_SANDBOX is required, not optional.
#
# RobustToolbox type-checks content assemblies against a whitelist that forbids
# System.Net.Sockets, System.Text.Json, SemaphoreSlim and Volatile. The copilot bridge needs
# all of them, so a sandboxed client refuses to start with TypeCheckFailedException.
#
# The sandbox exists to contain content assemblies supplied by the server you connect to.
# Running from source, the client loads Content.Client.dll from the local bin/ and downloads
# no code from anywhere, so there is nothing for the sandbox to contain here. That reasoning
# holds ONLY for a locally-built client against a trusted server. Do not carry this flag over
# to a general-purpose SS14 install.

param(
    [string]$Address = "udp://127.0.0.1:1212",
    [string]$Username = "Copilot"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll = Join-Path $root "client\bin\Content.Client\Content.Client.dll"

if (-not (Test-Path $dll)) {
    Write-Error "Client not built. Run: dotnet build client\Content.Client\Content.Client.csproj -c DebugOpt"
}

$env:ROBUST_DISABLE_SANDBOX = "1"

Write-Host "Sandbox disabled (required for the copilot bridge)." -ForegroundColor Yellow
Write-Host "Connecting to $Address as $Username" -ForegroundColor Cyan

& dotnet $dll --connect --connect-address $Address --username $Username
