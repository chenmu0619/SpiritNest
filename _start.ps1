# 启动已编译的开发版，不编译。
# 用法：.\ _start.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root "run\SpiritPerch.exe"
if (-not (Test-Path $exe)) {
  Write-Host "还没有开发版程序。先运行 .\_compile.ps1"
  exit 1
}
Start-Process $exe
Write-Host "已启动 $exe"
