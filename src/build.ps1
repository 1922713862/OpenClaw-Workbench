$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'build-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
& dotnet publish (Join-Path $PSScriptRoot 'JFWorkbench.csproj') -c Release --self-contained false -o (Join-Path $PSScriptRoot 'release') --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'JFWorkbench build failed.' }
Write-Output (Join-Path $PSScriptRoot 'release\JFWorkbench.exe')
