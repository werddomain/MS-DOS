param(
    [string]$DosBoxPath
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$v4SourceRoot = Join-Path $repoRoot "v4.0\src"
$cloneRoot = Join-Path $repoRoot "MsDos.Clone"
$artifactSource = Join-Path $v4SourceRoot "_OUT"
$artifactDest = Join-Path $cloneRoot "assets\msdos4"
$buildStatus = Join-Path $v4SourceRoot "BUILD.STATUS"
$buildLog = Join-Path $v4SourceRoot "BUILD.LOG"
$buildBat = Join-Path $v4SourceRoot "BUILD4.BAT"
$tempConf = Join-Path $env:TEMP "msdos4-build.conf"

if (-not (Test-Path $v4SourceRoot)) {
    throw "v4.0 source tree not found at: $v4SourceRoot"
}

if (-not $DosBoxPath) {
    $candidates = @(
        "C:\DOSBox-X\dosbox-x.exe",
        "C:\Program Files\DOSBox-X\dosbox-x.exe",
        "C:\Program Files (x86)\DOSBox-X\dosbox-x.exe",
        "C:\Program Files (x86)\DOSBox-0.74-3\DOSBox.exe"
    )

    $DosBoxPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $DosBoxPath -or -not (Test-Path $DosBoxPath)) {
    throw "DOSBox executable not found. Install DOSBox-X or provide -DosBoxPath."
}

$batchContent = @"
@echo off
set CL=
set LINK=
set MASM=
set COUNTRY=USA-MS
set BAKROOT=C:
set LIB=%BAKROOT%\TOOLS\LIB
set INIT=%BAKROOT%\TOOLS
set INCLUDE=%BAKROOT%\TOOLS\INC
set PATH=%BAKROOT%\TOOLS;%BAKROOT%

if exist BUILD.STATUS del BUILD.STATUS
if exist BUILD.LOG del BUILD.LOG
if not exist _OUT md _OUT
if exist _OUT\*.* del _OUT\*.*

echo Starting MS-DOS 4.0 build>BUILD.LOG
nmake>>BUILD.LOG
if errorlevel 1 goto failed

call CPY.BAT _OUT>>BUILD.LOG
if errorlevel 1 goto failed

echo BUILD_OK>BUILD.STATUS
goto done

:failed
echo BUILD_FAILED>BUILD.STATUS

:done
"@

$confContent = @"
[sdl]
fullscreen=false

[autoexec]
mount c $($v4SourceRoot.ToLowerInvariant())
c:
call BUILD4.BAT
exit
"@

Set-Content -Path $buildBat -Value $batchContent -Encoding ASCII
Set-Content -Path $tempConf -Value $confContent -Encoding ASCII

Write-Host "Using DOSBox: $DosBoxPath"
Write-Host "Launching build in DOSBox..."

$proc = Start-Process -FilePath $DosBoxPath -ArgumentList @("-noconfig", "-conf", $tempConf) -PassThru -Wait
Write-Host "DOSBox exited with code: $($proc.ExitCode)"

if (-not (Test-Path $buildStatus)) {
    throw "No BUILD.STATUS generated. Open DOSBox and run C:\BUILD4.BAT manually."
}

$status = (Get-Content $buildStatus -ErrorAction Stop | Select-Object -First 1).Trim()
if ($status -ne "BUILD_OK") {
    throw "MS-DOS 4.0 build failed. See log: $buildLog"
}

if (-not (Test-Path $artifactSource)) {
    throw "Expected artifact folder not found: $artifactSource"
}

New-Item -ItemType Directory -Force -Path $artifactDest | Out-Null
Copy-Item -Path (Join-Path $artifactSource "*") -Destination $artifactDest -Force

Write-Host "MS-DOS 4.0 artifacts copied to: $artifactDest"
