; LocalStream installer (Inno Setup 6). Build it with build-installer.ps1, which publishes the app
; first and passes the version in. Makes LocalStream-Setup-<version>.exe in installer\output.
;
; Installs for the current user by default (no admin prompt, and the app can update itself
; silently). Choosing "all users" installs to Program Files and also adds a firewall rule.

#ifndef AppVersion
  #define AppVersion "1.2.1"
#endif
#define AppName "LocalStream"
#define AppExe "LocalStreamPC.exe"

[Setup]
AppId={{6F3B2C1E-8A4D-4E7B-9C2F-5D1A0B7E3C94}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Manjeet Deswal
AppPublisherURL=https://github.com/manjeetdeswal/Local-Stream-Upnp---Http-Server-
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=LocalStream-Setup-{#AppVersion}
SetupIconFile=..\LocalStreamPC\Assets\app-icon.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Updates: close the running app, replace it, start it again.
CloseApplications=yes
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Files from an older version that are no longer shipped.
Type: filesandordirs; Name: "{app}\libvlc"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Lets phones and TVs reach the server without Windows asking (all-users installs only: needs admin).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; Check: IsAdminInstallMode
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=private"; Flags: runhidden; Check: IsAdminInstallMode
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; After a silent update started from the app, start the new version.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"; Check: IsAdminInstallMode
