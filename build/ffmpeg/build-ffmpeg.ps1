# Builds the slim LGPL ffmpeg.exe (build\ffmpeg\out\bin\ffmpeg.exe) by running build-ffmpeg.sh in
# MSYS2's UCRT64 environment. Needs MSYS2:  winget install MSYS2.MSYS2
param([string]$Version = "8.1.2")
$ErrorActionPreference = "Stop"

$bash = "C:\msys64\usr\bin\bash.exe"
if (-not (Test-Path $bash)) { throw "MSYS2 not found at C:\msys64. Install it with: winget install MSYS2.MSYS2" }

$env:MSYSTEM = "UCRT64"
$env:CHERE_INVOKING = "1"
$env:FFMPEG_VERSION = $Version
$script = (& "C:\msys64\usr\bin\cygpath.exe" -u (Join-Path $PSScriptRoot "build-ffmpeg.sh"))

# Passed as its own argument: the repo path can contain quotes (e.g. "Git Repo's").
# Everything is also written to build.log, since configure/make errors scroll by fast.
$log = Join-Path $PSScriptRoot "build.log"
& $bash -l $script 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $log
if ($LASTEXITCODE -ne 0) { throw "FFmpeg build failed (exit code $LASTEXITCODE). See $log" }
