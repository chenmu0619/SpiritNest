# Safe publish: never taskkill by default; write to staging then swap into run\.
# Usage:
#   .\ _pub.ps1              # graceful exit if running, then publish + start
#   .\ _pub.ps1 -ForceKill   # last resort force-kill (avoid while Cursor is open)
param(
  [switch]$ForceKill,
  [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\SpiritNest.App\SpiritNest.App.csproj"
$run = Join-Path $root "run"
$staging = Join-Path $root "run-staging"
$log = Join-Path $root "_pub_log.txt"

function Write-Log([string]$msg) {
  $msg | Out-File $log -Append -Encoding utf8
  Write-Host $msg
}

"" | Out-File $log -Encoding utf8
Write-Log "pub start $(Get-Date -Format o)"

$running = Get-Process -Name "SpiritPerch","SpiritNest" -ErrorAction SilentlyContinue
if ($running) {
  if ($ForceKill) {
    Write-Log "ForceKill: taskkill SpiritPerch.exe"
    cmd /c "taskkill /F /IM SpiritPerch.exe /T >nul 2>&1"
    cmd /c "taskkill /F /IM SpiritNest.exe /T >nul 2>&1"
    Get-Process -Name "SpiritPerch","SpiritNest" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
  }
  else {
    Write-Log "SpiritPerch is running — requesting graceful exit via pipe"
    Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.IO.Pipes;
public static class SpiritNestPipe {
  public static bool Send(string cmd) {
    try {
      using (var c = new NamedPipeClientStream(".", "SpiritNest.SingleInstance.Pipe.v3", PipeDirection.Out)) {
        c.Connect(1500);
        using (var w = new StreamWriter(c) { AutoFlush = true }) w.WriteLine(cmd);
        return true;
      }
    } catch { return false; }
  }
}
"@
    $sent = [SpiritNestPipe]::Send("exit")
    Write-Log "pipe exit sent=$sent"
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Get-Process -Name "SpiritPerch","SpiritNest" -ErrorAction SilentlyContinue)) {
      Start-Sleep -Milliseconds 250
    }
    if (Get-Process -Name "SpiritPerch","SpiritNest" -ErrorAction SilentlyContinue) {
      Write-Log "ERROR: SpiritPerch still running. Close it (tray Exit) or re-run with -ForceKill. Aborting to avoid Cursor/file-lock crash."
      exit 2
    }
  }
}

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Write-Log "dotnet publish -> $staging"
dotnet publish $proj -c Release -r win-x64 --self-contained true -o $staging
Write-Log "publish=$LASTEXITCODE"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path $run)) { New-Item -ItemType Directory -Path $run | Out-Null }
# Mirror staging into run without deleting run first while partial (robocopy /MIR)
robocopy $staging $run /MIR /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
$rc = $LASTEXITCODE
# robocopy 0-7 = success
if ($rc -ge 8) {
  Write-Log "ERROR: robocopy failed code=$rc"
  exit $rc
}
Write-Log "promoted staging -> run (robocopy=$rc)"

if (-not $NoStart) {
  Start-Process (Join-Path $run "SpiritPerch.exe")
  Start-Sleep -Seconds 2
}
Write-Log "done"
