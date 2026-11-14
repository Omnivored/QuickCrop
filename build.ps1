param([switch]$Standalone)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$dotnetExe = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnetExe -or -not (& $dotnetExe --list-sdks)) {
    $cachedSdk = Join-Path $env:USERPROFILE '.codex\cache\dotnet-sdk-10\dotnet.exe'
    if (Test-Path -LiteralPath $cachedSdk) { $dotnetExe = $cachedSdk }
    else { throw 'Install the .NET 10 SDK before building.' }
}
$kind = if ($Standalone) { 'Portable' } else { 'Light' }
$destination = Join-Path $PSScriptRoot "dist\QuickCrop-1.3.1-$kind"
$selfContained = if ($Standalone) { 'true' } else { 'false' }
& $dotnetExe publish QuickCrop.csproj -c Release -r win-x64 --self-contained $selfContained '-p:BaseOutputPath=bin/publish/' "-p:EnableCompressionInSingleFile=$selfContained" -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination $destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSES') -Destination $destination -Recurse -Force
Get-ChildItem -LiteralPath $destination | Select-Object Name,Length
