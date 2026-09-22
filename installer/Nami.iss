; Inno Setup script for Nami.
; Build:  ISCC.exe /DAppVersion=1.2.3 /DPublishDir=..\src\Nami\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish installer\Nami.iss
; Both defines are optional; defaults below point at the local publish output.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\src\Nami\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
#endif

#define AppName "Nami"
#define AppPublisher "subdiox"
#define AppURL "https://github.com/subdiox/nami"
#define AppExe "Nami.exe"

[Setup]
AppId={{7D1C4E0A-3B2F-4C9E-9E2B-6A1F0C2D5B77}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
; Per-user by default (file associations are written to HKCU); "dialog" lets an admin pick all-users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
#if AppVersion == "0.0.0"
  #define OutputName AppName + "-Setup-dev"
  #define DisplayVersion "0.0.0 (dev)"
#else
  #define OutputName AppName + "-Setup-" + AppVersion
  #define DisplayVersion AppVersion
#endif
OutputBaseFilename={#OutputName}
AppVerName={#AppName} {#DisplayVersion}
SetupIconFile=..\src\Nami\Assets\AppIcon.ico
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
; Ask running instances to close (Restart Manager) instead of failing on locked files.
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "associate"; Description: "{cm:AssocFileExtension,{#AppName},video/audio}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Registers HKCU file associations / namiplayer:// (the app exits right away for this switch).
Filename: "{app}\{#AppExe}"; Parameters: "--register"; Tasks: associate; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "unregister"

[UninstallDelete]
; Settings, history and the mpv config directory are the user's; leave them alone.
Type: filesandordirs; Name: "{app}"
