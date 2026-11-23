# Build SpiritNest-Setup-*.exe (does not start the installer).
# Requires: .NET SDK + Inno Setup 6/7 (ISCC.exe)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
$iscc = @(
  "D:\Program Files\Inno Setup 7\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
  Write-Host "未找到 Inno Setup（ISCC.exe）。请安装 6 或 7 后重试："
  Write-Host "  https://jrsoftware.org/isdl.php"
  exit 1
}
Write-Host "ISCC: $iscc"

Write-Host "dotnet publish -> dist\publish"
dotnet publish "src\SpiritNest.App\SpiritNest.App.csproj" -c Release -r win-x64 --self-contained true -o "dist\publish" -p:OfficialBuild=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "ISCC -> dist\SpiritPerch-Setup-*.exe"
& $iscc "installer\SpiritNest.iss"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem "dist\SpiritPerch-Setup-*.exe" | ForEach-Object {
  Write-Host ("OK: {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB))
}
