#ifndef MyAppVersion
  #define MyAppVersion "4.0.0"
#endif

#ifndef PublishDir
  #error PublishDir must point to the Windows publish directory
#endif

#ifndef OutputDir
  #define OutputDir "."
#endif

#define MyAppName "StrToolkit"
#define MyAppExeName "StrToolkit.exe"
#define MyAppIcon "..\..\src\StrToolkit\Assets\app-icon\StrToolkit.ico"
#define MyAppPublisher "changdy"
#define MyAppUrl "https://github.com/changdy/string_utils_v3"

[Setup]
AppId={{949C90B1-5C0B-42C1-8C7E-01CB33A5D9C5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=StrToolkit-Setup-win-x86
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#MyAppIcon}
PrivilegesRequired=lowest
ArchitecturesAllowed=x86compatible
CloseApplications=yes
RestartApplications=no
AppMutex=StrToolkit.Avalonia.SingleInstance
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "StrToolkit"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
