; DiskRelease 1.1.3 - Inno Setup 6.7+ installer.
; Compile: tools\InnoSetup\ISCC.exe installer.iss
; /TESTMODE=1 disables shortcuts and Add/Remove Programs registration for
; an isolated installation/uninstallation test in an explicit /DIR.
#define ProductName "简单查盘器"
#define ProductVersion "1.1.3"
#define ProductExe "DiskRelease.exe"
#define ProductIcon "DiskRelease-1.1.3.ico"

[Setup]
AppId={{C12CC255-DF7C-4D54-8658-5E51B7F50AA0}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppVerName={#ProductName} {#ProductVersion}
AppPublisher=DiskRelease
VersionInfoVersion={#ProductVersion}.0
VersionInfoDescription={#ProductName} 安装程序
DefaultDirName={localappdata}\Programs\DiskRelease
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableDirPage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
UsePreviousLanguage=no
WizardStyle=modern
SetupIconFile=src\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=dist
OutputBaseFilename={#ProductName}-安装包-{#ProductVersion}
UninstallDisplayName={#ProductName}
UninstallDisplayIcon={app}\{#ProductIcon}
Uninstallable=yes
CreateUninstallRegKey=not IsTestMode
CloseApplications=no
RestartApplications=no
SetupLogging=yes
UninstallLogging=yes
DisableWelcomePage=no
DisableReadyPage=no
DisableFinishedPage=no
InfoBeforeFile=安装说明.txt

[Languages]
Name: "chinesesimp"; MessagesFile: "installer-assets\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked; Check: not IsTestMode

[Files]
Source: "bin\{#ProductExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "src\app.ico"; DestDir: "{app}"; DestName: "{#ProductIcon}"; Flags: ignoreversion
#if FileExists("bin\DiskRelease.exe.config")
Source: "bin\DiskRelease.exe.config"; DestDir: "{app}"; Flags: ignoreversion
#endif
Source: "中文使用说明.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "licenses\Inno-Setup.txt"; DestDir: "{app}\licenses"; Flags: ignoreversion

[InstallDelete]
; Rename migration: remove only the shortcuts created by older installers.
; Keep unrelated files and remove the old Start menu folder only when empty.
Type: files; Name: "{autodesktop}\磁盘解占用.lnk"; Check: not IsTestMode
Type: files; Name: "{autoprograms}\磁盘解占用\磁盘解占用.lnk"; Check: not IsTestMode
Type: files; Name: "{autoprograms}\磁盘解占用\使用说明.lnk"; Check: not IsTestMode
Type: files; Name: "{autoprograms}\磁盘解占用\卸载磁盘解占用.lnk"; Check: not IsTestMode
Type: dirifempty; Name: "{autoprograms}\磁盘解占用"; Check: not IsTestMode

[Icons]
Name: "{autoprograms}\{#ProductName}\{#ProductName}"; Filename: "{app}\{#ProductExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#ProductIcon}"; Check: not IsTestMode
Name: "{autoprograms}\{#ProductName}\使用说明"; Filename: "{app}\中文使用说明.txt"; Check: not IsTestMode
Name: "{autoprograms}\{#ProductName}\卸载{#ProductName}"; Filename: "{uninstallexe}"; Check: not IsTestMode
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\{#ProductExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#ProductIcon}"; Tasks: desktopicon; Check: not IsTestMode

; No [Run] section: finishing installation never starts the app or a browser.
; No wildcard uninstall deletion: the uninstaller removes installed files only.

[Code]
function IsTestMode: Boolean;
begin
  Result := ExpandConstant('{param:TESTMODE|0}') = '1';
end;

function HasFramework48: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64,
    'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release);
  if not Result then
    Result := RegQueryDWordValue(HKLM32,
      'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release);
  if Result then
    Result := Release >= 528040;
end;

function InitializeSetup: Boolean;
begin
  Result := HasFramework48;
  if not Result then
    SuppressibleMsgBox('需要 Microsoft .NET Framework 4.8 或更新版本。' + #13#10 +
      '请先通过 Windows 更新或 Microsoft 官方安装程序安装，再重新运行本安装包。' + #13#10 +
      '本安装包不会联网下载或安装其他软件。', mbError, MB_OK, IDOK);
end;
