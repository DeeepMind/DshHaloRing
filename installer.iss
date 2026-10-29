; DSH HaloRing 安装包脚本（Inno Setup 6）
; 用 ISCC.exe 编译：ISCC.exe installer.iss
; 产物：dist\DshHaloRing-Setup-<version>.exe

#define MyAppName "DSH HaloRing"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "DshHaloRing"
#define MyAppExeName "DshHaloRing.exe"
#define MyAppId "{{6C2E9B14-8F3A-4D71-9A5E-1B7C3D9E2F40}"
#define PublishDir "publish-dir"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=DshHaloRing-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 仅支持 64 位 Windows 10/11
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; 托盘程序本身不需要管理员权限。
; 用 per-user 安装模式：装到 %LOCALAPPDATA% 下，避免 UAC 提权，
; 也让 HKCU 的「开机自启」写入真正生效（admin 模式下写 HKCU 会落到提权账户）。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
; 安装包自带图标（如果存在）
SetupIconFile=assets\app.ico

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; 开机自启：默认不勾选，用户可在托盘菜单随时改
Name: "autostart"; Description: "开机时自动启动 {#MyAppName}（可稍后在托盘菜单更改）"; GroupDescription: "附加选项:"; Flags: unchecked

[Files]
; 自包含发布目录的全部文件（含 .NET 运行时，用户无需另装）
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; DSH 状态桥插件已拆分为独立仓库 DeeepMind/dsh-haloring-status，
; 通过 dsh plugin add 安装，不再随安装包分发。

; 文档
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "docs\images\dark\预览拼图.png"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "docs\images\light\预览拼图.png"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
; 开始菜单
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "启动托盘状态指示灯"
Name: "{group}\{#MyAppName} 外观设置"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--settings"; WorkingDir: "{app}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
; 桌面快捷方式（可选，安装向导里可取消）
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: autostart

[Run]
; 安装完成后可选立即启动
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Registry]
; 开机自启（仅在用户勾选时写入 HKCU，与程序内的菜单开关共用同一个键）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DshHaloRing"; ValueData: """{app}\{#MyAppExeName}"" --tray"; Flags: uninsdeletevalue; Tasks: autostart

[UninstallDelete]
; 清理运行时生成的日志（配置在 %APPDATA%，保留用户设置）
Type: files; Name: "{app}\startup-trace.log"
Type: filesandordirs; Name: "{app}\data"

[Code]
// 安装前静默关闭正在运行的实例，避免文件占用导致安装失败。
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  ExeName: String;
begin
  if CurStep = ssInstall then
  begin
    ExeName := ExpandConstant('{#MyAppExeName}');
    Exec('taskkill.exe', '/F /IM ' + ExeName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
