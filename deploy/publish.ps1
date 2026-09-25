# Paketerar appen som self-contained win-x86 för IIS på Simply.
# Skarp deploy sker från Visual Studio via Properties\PublishProfiles\IISProfile.pubxml.
# Det här skriptet finns för att kunna inspektera publiceringsutdatan lokalt.

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\publish'),
    # R2R påverkar bara våra egna assemblies; runtime-paketets dll:er är redan R2R.
    # Det löser alltså INTE kallstarten, som är I/O-bunden. Kostar ca 3 MB extra.
    [switch]$NoReadyToRun
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot '..\src\Slideshow.Api\Slideshow.Api.csproj'

if (Test-Path $OutputPath) {
    Remove-Item $OutputPath -Recurse -Force
}

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x86',
    '--self-contained', 'true',
    '-o', $OutputPath,
    "/p:PublishReadyToRun=$((-not $NoReadyToRun.IsPresent).ToString().ToLower())"
)

Write-Host "dotnet $($arguments -join ' ')" -ForegroundColor DarkGray
& dotnet @arguments

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE"
}

$resolved = Resolve-Path $OutputPath
$files = Get-ChildItem $resolved -Recurse -File
$totalMb = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1MB, 1)

Write-Host ''
Write-Host "Output : $resolved"
Write-Host "Files  : $($files.Count)"
Write-Host "Size   : $totalMb MB"
Write-Host ''
Write-Host 'Skarp deploy görs från Visual Studio, inte härifrån.' -ForegroundColor Yellow
Write-Host '  - Web Deploy lagger ut app_offline sjalvt och skyddar App_Data via ExcludeApp_Data.'
Write-Host '  - App_Data\secrets.json pa servern maste innehalla Slideshow:AdminPasswordHash.'
Write-Host '    Skapa hashen med: Slideshow.Api.exe hash'
