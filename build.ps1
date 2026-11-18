# Build single-file exe: out/BatteryHub.exe
$ErrorActionPreference = "Stop"
dotnet publish "$PSScriptRoot\src\BatteryHub" -c Release -o "$PSScriptRoot\out"
Write-Host "Done: $PSScriptRoot\out\BatteryHub.exe"
