# Builds everything needed to ship Circularity, in one go:
#
#   artifacts\Circularity\               the app, self-contained (.NET runtime and FFmpeg included);
#                                         runs as-is, e.g. from a USB stick
#   artifacts\CircularitySetup-<ver>.exe  the installer
#
# Usage (from the repo root):  powershell -ExecutionPolicy Bypass -File build\build.ps1
# Needs: .NET 10 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup), and for the first build of
# FFmpeg, MSYS2 (winget install MSYS2.MSYS2).
param([switch]$RebuildFfmpeg)
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "src\Circularity\Circularity.csproj"
$artifacts = Join-Path $root "artifacts"
$appDir = Join-Path $artifacts "Circularity"
$installerDir = Join-Path $PSScriptRoot "installer"

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Size($path) {
    $bytes = if ((Get-Item $path).PSIsContainer) { (Get-ChildItem $path -Recurse -File | Measure-Object Length -Sum).Sum } else { (Get-Item $path).Length }
    "{0:N1} MB" -f ($bytes / 1MB)
}

# 1. FFmpeg (slow the first time; reused afterwards).
$ffmpeg = Join-Path $PSScriptRoot "ffmpeg\out\bin\ffmpeg.exe"
if ($RebuildFfmpeg -or -not (Test-Path $ffmpeg)) {
    Step "Building FFmpeg"
    & (Join-Path $PSScriptRoot "ffmpeg\build-ffmpeg.ps1")
}

# 2. The app, with the .NET runtime bundled so users don't need to install it.
Step "Publishing Circularity"
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
# MSBuild's publish step breaks if the output path contains an apostrophe (e.g. "Git Repo's"), so
# publish to a plain temporary folder and move the result.
$staging = Join-Path ([IO.Path]::GetTempPath()) "Circularity-publish"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $staging -p:DebugType=none -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
New-Item -ItemType Directory -Force $artifacts | Out-Null
Move-Item $staging $appDir
$version = (dotnet msbuild $project -getProperty:Version).Trim()

# 3. FFmpeg and the licences that must ship with everything bundled.
Step "Adding FFmpeg and licences"
Copy-Item $ffmpeg $appDir
$licenses = Join-Path $appDir "licenses"
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $PSScriptRoot "ffmpeg\out\licenses\*") $licenses
Copy-Item (Join-Path $installerDir "licenses\*") $licenses
Copy-Item (Join-Path $installerDir "THIRD-PARTY-NOTICES.txt") $appDir

# 4. Microsoft's WebView2 bootstrapper (the installer runs it only on PCs that lack WebView2).
$bootstrapper = Join-Path $installerDir "MicrosoftEdgeWebview2Setup.exe"
if (-not (Test-Path $bootstrapper)) {
    Step "Downloading the WebView2 bootstrapper from Microsoft"
    Invoke-WebRequest "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $bootstrapper
}
$signature = Get-AuthenticodeSignature $bootstrapper
if ($signature.Status -ne "Valid" -or $signature.SignerCertificate.Subject -notmatch "O=Microsoft Corporation") {
    Remove-Item $bootstrapper
    throw "The WebView2 bootstrapper isn't validly signed by Microsoft; deleted it."
}

# 5. The installer.
Step "Building the installer"
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }
& $iscc /Q "/DAppVersion=$version" "/DPublishDir=$appDir" "/DOutputDir=$artifacts" (Join-Path $installerDir "Circularity.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
$setup = Join-Path $artifacts "CircularitySetup-$version.exe"

Step "Done"
Write-Host ("App folder : {0}  ({1}; the exe is {2})" -f $appDir, (Size $appDir), (Join-Path $appDir "Circularity.exe"))
Write-Host ("Installer  : {0}  ({1})" -f $setup, (Size $setup))
