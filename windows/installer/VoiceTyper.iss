; VoiceTyper (unified) Inno Setup script.
; Per-user install under %LOCALAPPDATA%\Programs\VoiceTyper — no UAC elevation prompt.
; Invoked from build.bat as:
;   iscc.exe /DAppVersion=3.2.1 /DTargetRid=win-x64 /DTargetArch=x64 installer\VoiceTyper.iss
;
; 见 windows/DESIGN.md §7 D9：目录式部署 + Inno Setup 安装包，不用 PublishSingleFile 自解压。

#ifndef AppVersion
  #define AppVersion "3.2.1"
#endif
#ifndef TargetRid
  #define TargetRid "win-x64"
#endif
#ifndef TargetArch
  #define TargetArch "x64"
#endif

[Setup]
AppId={{B7E4C2A1-6F3D-4E9B-9A2C-1D8F5E6A3B70}
AppName=VoiceTyper
AppVersion={#AppVersion}
AppPublisher=VoiceTyper
DefaultDirName={localappdata}\Programs\VoiceTyper
DefaultGroupName=VoiceTyper
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=VoiceTyper-{#AppVersion}-{#TargetRid}-setup
Compression=lzma2
SolidCompression=yes
#if TargetArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
SetupIconFile=..\Assets\icon.ico
UninstallDisplayIcon={app}\VoiceTyper.exe
WizardStyle=modern

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\dist\{#TargetRid}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\VoiceTyper"; Filename: "{app}\VoiceTyper.exe"
Name: "{group}\卸载 VoiceTyper"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\VoiceTyper.exe"; Description: "启动 VoiceTyper"; Flags: nowait postinstall skipifsilent unchecked

[Registry]
; 卸载时清理本应用在运行期写入的开机自启项（若用户启用过），只动本应用拥有的值。
; 安装时不创建该值——自启由应用内 StartupRegistration 按用户选择写入（R4-2）。
; 注：uninsdeletevalue 对运行期创建的值的清理效果需在真机卸载时确认。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "VoiceTyper"; Flags: uninsdeletevalue

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
