# 打包安装程序。先显示当前版本号，确认后才编译。
# 不关闭正在运行的开发版，也不覆盖 run\。
# 用法：
#   .\ _pack.ps1
#   或双击 _pack.cmd
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csprojPath = Join-Path $root "src\SpiritNest.App\SpiritNest.App.csproj"
$issPath = Join-Path $root "installer\SpiritNest.iss"

$csproj = Get-Content $csprojPath -Raw -Encoding UTF8
$iss = Get-Content $issPath -Raw -Encoding UTF8

function Read-Match([string]$text, [string]$pattern) {
  $m = [regex]::Match($text, $pattern)
  if ($m.Success) { return $m.Groups[1].Value.Trim() }
  return "（未读到）"
}

$asmVersion = Read-Match $csproj '<Version>([^<]+)</Version>'
$aboutVersion = Read-Match $csproj "OfficialBuild\)' == 'true'"">([^<]+)</InformationalVersion>"
$setupVersion = Read-Match $iss '#define MyAppVersion "([^"]+)"'

Write-Host ""
Write-Host "打包前请确认版本号"
Write-Host "  安装包版本  installer\SpiritNest.iss"
Write-Host "              MyAppVersion = $setupVersion"
Write-Host "  程序版本    src\SpiritNest.App\SpiritNest.App.csproj"
Write-Host "              Version = $asmVersion"
Write-Host "  关于页版本  同一 csproj 里安装版 InformationalVersion = $aboutVersion"
Write-Host "  将生成      dist\SpiritPerch-Setup-$setupVersion.exe"
Write-Host ""
if ($asmVersion -ne $setupVersion -or $aboutVersion -ne $setupVersion) {
  Write-Host "这三处版本号不一致。安装包文件名以安装脚本为准。" -ForegroundColor Yellow
  Write-Host ""
}
Write-Host "要改版本号就先改这两个文件并保存，再回到这里继续。"
Write-Host ""

$answer = Read-Host "继续打包？输入 Y 继续，其他键取消"
if ($answer -ne "Y" -and $answer -ne "y") {
  Write-Host "已取消。"
  exit 0
}

& (Join-Path $root "installer\build-setup.ps1")
exit $LASTEXITCODE
