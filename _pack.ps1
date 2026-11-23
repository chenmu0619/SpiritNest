# 打包安装程序。先显示当前版本号，确认后才编译。
# 不关闭正在运行的开发版，也不覆盖 run\。
# 用法：
#   .\ _pack.ps1
#   或双击 _pack.cmd
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionPath = Join-Path $root "version.txt"
if (-not (Test-Path -LiteralPath $versionPath)) {
  Write-Host "找不到 version.txt。"
  exit 1
}
$version = (Get-Content $versionPath -Raw -Encoding UTF8).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
  Write-Host "version.txt 是空的。"
  exit 1
}

Write-Host ""
Write-Host "打包前请确认版本号"
Write-Host "  版本号      version.txt = $version"
Write-Host "  程序版本、关于页、安装包都引用这一处。"
Write-Host "  将生成      dist\SpiritPerch-Setup-$version.exe"
Write-Host ""
Write-Host "要改版本号就先改 version.txt 并保存，再回到这里继续。"
Write-Host ""

$answer = Read-Host "继续打包？输入 Y 继续，其他键取消"
if ($answer -ne "Y" -and $answer -ne "y") {
  Write-Host "已取消。"
  exit 0
}

& (Join-Path $root "installer\build-setup.ps1")
exit $LASTEXITCODE
