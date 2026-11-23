param(
    [Parameter(Mandatory = $true)][string]$UninstallExe,
    [Parameter(Mandatory = $true)][string]$ImagePath,
    [Parameter(Mandatory = $true)][int]$ParentPid
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
[System.Windows.Forms.Application]::SetCompatibleTextRenderingDefault($false)

$script:busy = $false
$script:finished = $false

function Test-AppRunning {
    return [bool](Get-Process -Name 'SpiritPerch' -ErrorAction SilentlyContinue)
}

function Pump([int]$Milliseconds) {
    $end = [DateTime]::UtcNow.AddMilliseconds($Milliseconds)
    while ([DateTime]::UtcNow -lt $end) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 40
    }
}

$form = New-Object System.Windows.Forms.Form
$form.Text = '卸载 - 灵栖'
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.StartPosition = 'CenterScreen'
$form.ClientSize = New-Object System.Drawing.Size(596, 432)
$form.BackColor = [System.Drawing.SystemColors]::Control
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

$surface = New-Object System.Windows.Forms.Panel
$surface.Location = New-Object System.Drawing.Point(0, 0)
$surface.Size = New-Object System.Drawing.Size(596, 376)
$surface.BackColor = [System.Drawing.Color]::White
$form.Controls.Add($surface)

$picture = New-Object System.Windows.Forms.PictureBox
$picture.Location = New-Object System.Drawing.Point(0, 0)
$picture.Size = New-Object System.Drawing.Size(197, 376)
$picture.SizeMode = 'StretchImage'
$picture.BackColor = [System.Drawing.Color]::White
if (Test-Path -LiteralPath $ImagePath) {
    $loaded = [System.Drawing.Image]::FromFile($ImagePath)
    $clone = New-Object System.Drawing.Bitmap($loaded)
    $loaded.Dispose()
    $picture.Image = $clone
}
$surface.Controls.Add($picture)

$title = New-Object System.Windows.Forms.Label
$title.Text = '卸载灵栖'
$title.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 12, [System.Drawing.FontStyle]::Bold)
$title.Location = New-Object System.Drawing.Point(211, 19)
$title.Size = New-Object System.Drawing.Size(361, 32)
$title.BackColor = [System.Drawing.Color]::White
$surface.Controls.Add($title)

$appVersion = ""
$appExe = Join-Path (Split-Path -Parent $UninstallExe) "SpiritPerch.exe"
if (Test-Path -LiteralPath $appExe) {
    $appVersion = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($appExe).ProductVersion).Trim()
}
$versionLabel = if ($appVersion) { " $appVersion" } else { "" }

$info = New-Object System.Windows.Forms.Label
$info.Text = "这将从您的电脑上卸载灵栖$versionLabel。`r`n`r`n勾选要在卸载时执行的操作。`r`n`r`n点击「确定卸载」后才会开始，点击「取消」则不做任何更改。"
$info.Location = New-Object System.Drawing.Point(211, 64)
$info.Size = New-Object System.Drawing.Size(361, 110)
$info.BackColor = [System.Drawing.Color]::White
$surface.Controls.Add($info)

$restore = New-Object System.Windows.Forms.CheckBox
$restore.Text = '还原快捷方式'
$restore.Location = New-Object System.Drawing.Point(211, 186)
$restore.Size = New-Object System.Drawing.Size(361, 22)
$restore.BackColor = [System.Drawing.Color]::White
$restore.Checked = $false
$surface.Controls.Add($restore)

$keep = New-Object System.Windows.Forms.CheckBox
$keep.Text = '保留用户数据'
$keep.Location = New-Object System.Drawing.Point(211, 220)
$keep.Size = New-Object System.Drawing.Size(361, 22)
$keep.BackColor = [System.Drawing.Color]::White
$keep.Checked = $true
$surface.Controls.Add($keep)

$progress = New-Object System.Windows.Forms.ProgressBar
$progress.Location = New-Object System.Drawing.Point(211, 186)
$progress.Size = New-Object System.Drawing.Size(361, 22)
$progress.Minimum = 0
$progress.Maximum = 100
$progress.Value = 0
$progress.Style = 'Continuous'
$progress.Visible = $false
$surface.Controls.Add($progress)

$status = New-Object System.Windows.Forms.Label
$status.Text = ''
$status.Location = New-Object System.Drawing.Point(211, 260)
$status.Size = New-Object System.Drawing.Size(361, 90)
$status.BackColor = [System.Drawing.Color]::White
$surface.Controls.Add($status)

$rule = New-Object System.Windows.Forms.Panel
$rule.Location = New-Object System.Drawing.Point(0, 376)
$rule.Size = New-Object System.Drawing.Size(596, 1)
$rule.BackColor = [System.Drawing.Color]::FromArgb(220, 220, 220)
$form.Controls.Add($rule)

$cancel = New-Object System.Windows.Forms.Button
$cancel.Text = '取消'
$cancel.Size = New-Object System.Drawing.Size(108, 28)
$cancel.Location = New-Object System.Drawing.Point(476, 392)
$form.Controls.Add($cancel)

$ok = New-Object System.Windows.Forms.Button
$ok.Text = '确定卸载'
$ok.Size = New-Object System.Drawing.Size(108, 28)
$ok.Location = New-Object System.Drawing.Point(358, 392)
$form.AcceptButton = $ok
$form.CancelButton = $cancel
$form.Controls.Add($ok)

function Update-RunningState {
    if ($script:busy -or $script:finished) { return }
    if (Test-AppRunning) {
        $status.Text = '检测到灵栖正在运行，请先退出。退出后可在此继续卸载。'
        $ok.Enabled = $false
    }
    else {
        if ($status.Text.StartsWith('检测到')) { $status.Text = '' }
        $ok.Enabled = $true
    }
}

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 400
$timer.Add_Tick({ Update-RunningState })
$timer.Start()
Update-RunningState

$cancel.Add_Click({
    if (-not $script:busy) { $form.Close() }
})

$ok.Add_Click({
    if ($script:finished) {
        $form.Close()
        return
    }
    if ($script:busy -or (Test-AppRunning)) { return }

    $script:busy = $true
    $timer.Stop()
    $restore.Visible = $false
    $keep.Visible = $false
    $ok.Enabled = $false
    $cancel.Enabled = $false
    $title.Text = '正在卸载'
    $info.Text = '正在从这台电脑上移除灵栖，请稍候。'
    $status.Text = '正在卸载灵栖…'
    $progress.Visible = $true
    $progress.Value = 0
    $form.Refresh()

    $appDir = Split-Path -Parent $UninstallExe
    $initial = @(Get-ChildItem -LiteralPath $appDir -Recurse -File -Force -ErrorAction SilentlyContinue).Count
    if ($initial -lt 1) { $initial = 1 }

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Get-Process -Id $ParentPid -ErrorAction SilentlyContinue) -and ([DateTime]::UtcNow -lt $deadline)) {
        Pump 200
    }
    Pump 600

    $argList = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if ($restore.Checked) { $argList += '/RESTORE' }
    if (-not $keep.Checked) { $argList += '/PURGE' }

    $proc = Start-Process -FilePath $UninstallExe -ArgumentList $argList -PassThru
    while (-not $proc.HasExited) {
        $left = @(Get-ChildItem -LiteralPath $appDir -Recurse -File -Force -ErrorAction SilentlyContinue).Count
        $done = [int](100 * ($initial - $left) / $initial)
        if ($done -lt 0) { $done = 0 }
        if ($done -gt 95) { $done = 95 }
        if ($done -gt $progress.Value) { $progress.Value = $done }
        Pump 120
    }
    $proc.Refresh()
    $progress.Value = 100

    $script:busy = $false
    $script:finished = $true
    $cancel.Visible = $false
    $ok.Enabled = $true
    if ($proc.ExitCode -eq 0) {
        $status.Text = '灵栖已卸载。'
        $ok.Text = '完成'
    }
    else {
        $status.Text = '卸载没有完成。'
        $ok.Text = '关闭'
    }
})

$form.Add_FormClosing({
    param($sender, $e)
    if ($script:busy) { $e.Cancel = $true }
})

$form.Add_Shown({ $form.Activate() | Out-Null })
[void]$form.ShowDialog()
$timer.Stop()
if ($picture.Image) { $picture.Image.Dispose() }
$form.Dispose()
