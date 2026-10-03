; VoiceTyper (unified) Inno Setup script.
; Per-user install under %LOCALAPPDATA%\Programs\VoiceTyper — no UAC elevation prompt.
; Invoked from build.bat as:
;   iscc.exe /DAppVersion=3.5.1 /DTargetRid=win-x64 /DTargetArch=x64 installer\VoiceTyper.iss
;
; 见 windows/DESIGN.md §7 D9：目录式部署 + Inno Setup 安装包，不用 PublishSingleFile 自解压。

#ifndef AppVersion
  #define AppVersion "3.5.1"
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
Filename: "{app}\VoiceTyper.exe"; Description: "启动 VoiceTyper"; Flags: nowait postinstall skipifsilent

[Registry]
; 卸载时清理本应用在运行期写入的开机自启项（若用户启用过），只动本应用拥有的值。
; 安装时不创建该值——自启由应用内 StartupRegistration 按用户选择写入（R4-2）。
; 注：uninsdeletevalue 对运行期创建的值的清理效果需在真机卸载时确认。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "VoiceTyper"; Flags: uninsdeletevalue

[InstallDelete]
; 升级旧自包含版本时清理曾随应用分发的运行时，避免轻量版目录仍保留 100 多 MiB 旧文件。
#include "legacy-self-contained-files.iss"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[CustomMessages]
chinesesimplified.DesktopRuntimeRequired=VoiceTyper 需要已安装的 .NET 10 桌面运行时（{#TargetArch}）。%n%n请先安装 Microsoft .NET Desktop Runtime 10.0，再重新运行安装程序。普通 .NET Runtime、.NET 8/9 或其他架构的运行时不能代替。%n%n下载地址：https://dotnet.microsoft.com/download/dotnet/10.0%n%n此安装包不包含或自动下载 .NET 运行时。
english.DesktopRuntimeRequired=VoiceTyper requires an installed .NET 10 Desktop Runtime ({#TargetArch}).%n%nInstall Microsoft .NET Desktop Runtime 10.0, then run this installer again. The regular .NET Runtime, .NET 8/9, or a runtime for another architecture is insufficient.%n%nDownload: https://dotnet.microsoft.com/download/dotnet/10.0%n%nThis installer does not bundle or download .NET.

[Code]
function HasNet10FrameworkInView(RootKey: HKEY; Framework: String): Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  { 官方安装器在注册表记录每个架构的共享框架版本；同时兼容两种注册表视图。 }
  if not RegGetValueNames(RootKey,
    'SOFTWARE\dotnet\Setup\InstalledVersions\{#TargetArch}\sharedfx\' + Framework,
    Versions) then
    Exit;
  for Index := 0 to GetArrayLength(Versions) - 1 do
  begin
    { 仅接受稳定的 10.0.x；预览版本和其他主版本不满足默认框架解析策略。 }
    if (Copy(Versions[Index], 1, 5) = '10.0.') and
      (StrToIntDef(Copy(Versions[Index], 6, Length(Versions[Index])), -1) >= 0) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function HasNet10Framework(Framework: String): Boolean;
begin
  Result := HasNet10FrameworkInView(HKLM32, Framework) or
    HasNet10FrameworkInView(HKLM64, Framework);
end;

function InitializeSetup(): Boolean;
begin
  Result := HasNet10Framework('Microsoft.NETCore.App') and
    HasNet10Framework('Microsoft.WindowsDesktop.App');
  if not Result then
  begin
    Log('缺少目标架构的 .NET 10 Desktop Runtime，停止安装。');
    SuppressibleMsgBox(CustomMessage('DesktopRuntimeRequired'), mbError, MB_OK, IDOK);
  end;
end;
