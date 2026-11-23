; 灵栖 — Inno Setup 安装包
; 1) 先发布：
;    dotnet publish src\SpiritNest.App\SpiritNest.App.csproj -c Release -r win-x64 --self-contained true -o dist\publish
; 2) 再编译本脚本（需安装 Inno Setup 6/7），例如：
;    "D:\Program Files\Inno Setup 7\ISCC.exe" installer\SpiritNest.iss
;    或直接：.\installer\build-setup.ps1
; 产出：dist\SpiritPerch-Setup-{version}.exe

#define MyAppName "灵栖"
#define MyAppNameZh "灵栖"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "SpiritNest"
#define MyAppExeName "SpiritPerch.exe"
#define PublishDir "..\dist\publish"
#define BrandDir "brand"

[Setup]
AppId={{A7C3E9F1-4B2D-4E8A-9C1F-SpiritNest0001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (C) {#MyAppPublisher}
DefaultDirName={autopf}\SpiritPerch
; 曾安装过时，目录页仍显示，并预填上次的安装目录
UsePreviousAppDir=yes
DisableDirPage=no
DefaultGroupName={#MyAppNameZh}
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableFinishedPage=no
OutputDir=..\dist
OutputBaseFilename=SpiritPerch-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=120
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
ShowLanguageDialog=no
; 品牌图（欢迎页 / 完成页左侧大图 + 右上角小图）
SetupIconFile={#BrandDir}\setup.ico
WizardImageFile={#BrandDir}\wizard-image.bmp
WizardSmallImageFile={#BrandDir}\wizard-small.bmp
WizardImageStretch=yes
WizardImageBackColor=clNone

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Messages]
WelcomeLabel1=欢迎使用灵栖
WelcomeLabel2=这将在您的电脑上安装 [name/ver]。%n%n灵栖是屏幕顶部的灵动岛：收纳常用入口，悬停展开，离开后收起。%n%n建议在继续之前关闭其他正在运行的应用程序。
FinishedHeadingLabel=完成安装灵栖
FinishedLabelNoIcons=灵栖已安装到您的电脑。
FinishedRestartLabel=要完成安装，计算机必须重新启动。是否立即重新启动？
ClickFinish=单击「完成」退出安装向导。
SelectTasksLabel2=请选择要执行的附加任务，然后单击「下一步」。
WizardSelectTasks=选择附加任务
WizardReady=准备安装
ReadyLabel1=安装程序准备将灵栖安装到您的电脑。
ReadyLabel2a=单击「安装」继续，或单击「上一步」检查 / 更改设置。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked
Name: "autostart"; Description: "开机时启动灵栖"; GroupDescription: "启动:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#BrandDir}\wizard-image.bmp"; DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "uninstall-ui.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\SpiritNest.ico"
Name: "{group}\卸载 {#MyAppNameZh}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppNameZh}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\SpiritNest.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动灵栖"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  UninstallRestoreShortcuts: Boolean;
  UninstallKeepUserData: Boolean;

function HasCmdParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function GetCurrentProcessId: Cardinal;
  external 'GetCurrentProcessId@kernel32.dll stdcall';

{ One window for the whole uninstall. This process only launches it, then
  exits so the uninstall log is free. The window itself starts the silent
  uninstall and keeps the same page for the running-app notice and progress. }
function InitializeUninstall(): Boolean;
var
  Script, Params: String;
  ResultCode: Integer;
begin
  if UninstallSilent then
  begin
    UninstallRestoreShortcuts := HasCmdParam('/RESTORE');
    UninstallKeepUserData := not HasCmdParam('/PURGE');
    Result := True;
    Exit;
  end;

  Result := False;
  Script := ExpandConstant('{%TEMP}\SpiritNest-uninstall-ui.ps1');
  if not CopyFile(ExpandConstant('{app}\uninstall-ui.ps1'), Script, False) then
    Exit;
  Params :=
    '-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + Script + '"' +
    ' -UninstallExe "' + ExpandConstant('{uninstallexe}') + '"' +
    ' -ImagePath "' + ExpandConstant('{app}\Assets\wizard-image.bmp') + '"' +
    ' -ParentPid ' + IntToStr(GetCurrentProcessId);
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params,
    '', SW_HIDE, ewNoWait, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Params: String;
begin
  if CurStep <> ssPostInstall then
    Exit;
  if WizardIsTaskSelected('autostart') then
    Params := '--set-autostart'
  else
    Params := '--clear-autostart';
  if not Exec(ExpandConstant('{app}\{#MyAppExeName}'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := 1;
  if WizardIsTaskSelected('autostart') and (ResultCode <> 0) then
    MsgBox('开机启动未能写入计划任务。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Params: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  Params := '--uninstall-finalize';
  if UninstallRestoreShortcuts then
    Params := Params + ' --restore-shortcuts';
  if not UninstallKeepUserData then
    Params := Params + ' --purge-user-data';

  if UninstallRestoreShortcuts or (not UninstallKeepUserData) then
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--clear-autostart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SpiritPerch');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SpiritNest');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'SpiritPerch');
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'SpiritNest');
  Exec('schtasks.exe', '/Delete /TN "SpiritPerch\Autostart" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('schtasks.exe', '/Delete /TN "SpiritNest\Autostart" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
