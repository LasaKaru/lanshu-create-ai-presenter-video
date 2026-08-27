<#
.SYNOPSIS
  Publishes self-contained, single-file builds of the studio and the CLI.
.EXAMPLE
  .\build\publish.ps1
  .\build\publish.ps1 -Rid linux-x64 -Output C:\out
#>
[CmdletBinding()]
param(
    [string]$Rid = 'win-x64',
    [string]$Output
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root "artifacts\$Rid" }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 8 SDK is required. See https://dotnet.microsoft.com/download'
}

Write-Host "Publishing $Rid to $Output"
if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }

$common = @(
    '-c', 'Release', '-r', $Rid, '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:EnableWindowsTargeting=true', '--nologo'
)

dotnet publish (Join-Path $root 'src\Lanshu.Presenter.App\Lanshu.Presenter.App.csproj') @common -o $Output
dotnet publish (Join-Path $root 'src\Lanshu.Presenter.Cli\Lanshu.Presenter.Cli.csproj') @common -o $Output

Copy-Item (Join-Path $root '..\LICENSE') (Join-Path $Output 'LICENSE.txt') -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root 'DOWNLOAD.md') (Join-Path $Output 'README.txt') -ErrorAction SilentlyContinue

Write-Host ''
Get-ChildItem $Output
