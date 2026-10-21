param([string]$Output = (Join-Path $PSScriptRoot '..\FlightGuard-v0.6-Windows'))
$ErrorActionPreference = 'Stop'
dotnet publish (Join-Path $PSScriptRoot 'FlightGuard.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:EnableSingleFileAnalyzer=false -p:EnableTrimAnalyzer=false -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if (Test-Path (Join-Path $PSScriptRoot 'Release')) { Copy-Item -Path (Join-Path $PSScriptRoot 'Release\*') -Destination $Output -Recurse -Force }
if (Test-Path (Join-Path $PSScriptRoot 'Assets')) { New-Item -ItemType Directory -Path (Join-Path $Output 'Brand') -Force | Out-Null; Copy-Item -Path (Join-Path $PSScriptRoot 'Assets\*') -Destination (Join-Path $Output 'Brand') -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $Output 'LICENSE-App.txt') -Force
Write-Output "Application published to $Output. Use python fetch_sounds.py <output> to reproduce the licensed sound collection."
