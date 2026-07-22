; Inno Setup script for Widget Subscription (#16 / spec §5).
; Wraps the self-contained single-file WidgetSubscription.exe (win-x64, .NET 8 + SkiaSharp bundled)
; so a clean machine needs nothing preinstalled. Build: run installer/build.ps1 (publishes, then ISCC).
; Not code-signed — SmartScreen "unknown publisher" is the accepted trade-off for a personal utility.

#define MyAppName "Widget Subscription"
#define MyAppExeName "WidgetSubscription.exe"
#define MyAppPublisher "VladPatr96"
#define MyAppVersion "1.0.0"

; Folder holding the published single-file exe; override with /DSourceDir=... when invoking ISCC.
#ifndef SourceDir
  #define SourceDir "..\src\App\bin\Release\net8.0\win-x64\publish"
#endif

[Setup]
AppId={{7F3B1E2A-9C44-4D8E-9F1A-2B6D5C0A7E10}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\WidgetSubscription
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
; Matches the app's Local\ single-instance mutex so Setup asks to close a running/autostarted widget.
AppMutex=WidgetSubscription.SingleInstance
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=dist
OutputBaseFilename=WidgetSubscription-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "autostart"; Description: "Запускать при входе в систему"; GroupDescription: "Автозапуск:"
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked

[Files]
; The self-contained single-file publish output — one exe, no extra runtime files.
Source: "{#SourceDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Per-user autostart (HKCU\...\Run), enabled by default via the "autostart" task; mirrors the tray toggle.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "WidgetSubscription"; ValueData: """{app}\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// On uninstall, offer to remove the own-login credentials (%LOCALAPPDATA%\WidgetSubscription);
// default is to keep them so a reinstall does not force a fresh login (#16).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\WidgetSubscription');
    if DirExists(DataDir) then
      if MsgBox('Удалить сохранённые данные и разлогинить виджет?' + #13#10 +
                '«Нет» — сохранить вход для переустановки.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
