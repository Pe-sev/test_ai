# Fas 0: paketerar appen som self-contained win-x86 för IIS på Simply.
# Full deploy-automation (app_offline + filöverföring) byggs i fas 7 när vi vet
# om servern erbjuder FTP, FTPS eller SFTP.

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\publish'),
    # Kallstarten på Simply mättes till 164 s utan R2R, därför är den på som standard.
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
Write-Host 'Innan första uppladdningen till Simply:' -ForegroundColor Yellow
Write-Host '  1. Skapa App_Data\logs pa servern - ANCM skapar den inte sjalv och'
Write-Host '     stdout-loggen behovs just nar appen inte startar.'
Write-Host '  2. Kontrollera att app poolen har "Enable 32-bit Applications" = True.'
Write-Host '  3. Ladda upp hela innehallet i publish-mappen till sitens rot.'
