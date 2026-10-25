; Layal per-user Windows installer.
; Build from the repository root after publishing a self-contained win-x64 folder.
; The app is installed separately from %LOCALAPPDATA%\Layal so upgrades and uninstall
; never delete encrypted user settings, conversations, tasks, memories, or skills.

#ifndef AppVersion
  #define AppVersion "0.5.2"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{9B63EA2A-5AE3-4DD8-9D87-26E10F24E970}
AppName=Layal
AppVersion={#AppVersion}
AppVerName=Layal {#AppVersion}
AppPublisher=Layal Project
DefaultDirName={localappdata}\Programs\Layal
DefaultGroupName=Layal
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UsePreviousAppDir=yes
Uninstallable=yes
UninstallDisplayName=Layal
UninstallDisplayIcon={app}\Layal.exe
CloseApplications=yes
CloseApplicationsFilter=Layal.exe
RestartApplications=no
RestartIfNeededByRun=no
SetupIconFile=..\src\Layal.Desktop\Assets\layal-tray.ico
OutputDir=..\artifacts\distribution
OutputBaseFilename=Layal-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
VersionInfoCompany=Layal Project
VersionInfoDescription=Layal Windows desktop companion installer
VersionInfoProductName=Layal
VersionInfoProductVersion={#AppVersion}.0
VersionInfoVersion={#AppVersion}.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Layal"; Filename: "{app}\Layal.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Layal"; Filename: "{app}\Layal.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Layal.exe"; Description: "Launch Layal"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent
