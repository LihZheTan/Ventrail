# Builds the release package of Ventrail.
#
#   powershell -ExecutionPolicy Bypass -File build\package.ps1
#
# Produces, in .\artifacts:
#   Ventrail-<version>-win-x64\        the app, ready to run (Ventrail.exe), no .NET install needed
#   Ventrail-<version>-win-x64.zip     the same folder zipped (portable: unzip anywhere and run)
#   Ventrail-<version>-Setup.exe       an installer, only if Inno Setup 6 is installed (installer\Ventrail.iss)
param(
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $root 'Ventrail\Ventrail.csproj'
[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$name = "Ventrail-$version-$Runtime"
$artifacts = Join-Path $root 'artifacts'
$out = Join-Path $artifacts $name

Write-Host "Packaging Ventrail $version for $Runtime" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host "`n1. Running the test suites..." -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tests\run-all.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: not packaging." }
}

Write-Host "`n2. Publishing (self-contained, so users don't need to install .NET)..." -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }   # only our own previous build output
& dotnet publish $project -c Release -r $Runtime --self-contained true -o $out -v q --nologo `
    -p:DebugType=none -p:SatelliteResourceLanguages=en
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# The ML.NET model and the native Git library must be next to the exe.
foreach ($required in @('Ventrail.exe', 'MLModels\ventrail_classifier.zip')) {
    if (-not (Test-Path (Join-Path $out $required))) { throw "Missing from the package: $required" }
}
if (-not (Get-ChildItem $out -Recurse -Filter 'git2-*.dll')) { throw "Missing from the package: native Git library (git2-*.dll)" }
Copy-Item (Join-Path $root 'docs\USER_GUIDE.md') (Join-Path $out 'USER_GUIDE.md')

Write-Host "`n3. Zipping..." -ForegroundColor Cyan
$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "   $zip ($sizeMb MB)"

Write-Host "`n4. Installer..." -ForegroundColor Cyan
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc "/DAppVersion=$version" "/DSourceDir=$out" "/O$artifacts" (Join-Path $root 'installer\Ventrail.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
} else {
    Write-Host "   Inno Setup 6 is not installed, so no Setup.exe was made (the zip works on its own)."
    Write-Host "   To also build an installer: install Inno Setup 6 (jrsoftware.org) and run this script again."
}

Write-Host "`nDone: $artifacts" -ForegroundColor Green
