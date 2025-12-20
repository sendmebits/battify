; Battify Installer Script for Inno Setup
; https://jrsoftware.org/isinfo.php

#define MyAppName "Battify"
#define MyAppVersion "1.0.3"
#define MyAppPublisher "sendmebits"
#define MyAppURL "https://github.com/sendmebits/battify"
#define MyAppExeName "Battify.exe"

[Setup]
; App identification
AppId={{8F2E5C3A-B4D1-4E8F-9A2B-1C3D4E5F6A7B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

; Installation directories
; Default to Program Files (requires admin)
; User can choose per-user install which goes to LocalAppData
DefaultDirName={autopf64}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Output settings
OutputDir=..\installer-output
OutputBaseFilename=Battify-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\favicon.ico

; Compression
Compression=lzma2
SolidCompression=yes

; Windows version requirements (Windows 10+)
MinVersion=10.0

; Privileges - admin required for Program Files, but user can override
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

; Architecture - 64-bit only
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

; Suppress warning about per-user registry with admin install - this is intentional
; The startup entry should be per-user (HKCU) even for admin installs
UsedUserAreasWarning=no

; Visual settings
WizardStyle=modern
WizardSizePercent=100

; Uninstaller
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start {#MyAppName} when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
; Main executable and dependencies from standalone build
Source: "..\Battify-Standalone\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Assets
Source: "..\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
; Desktop (optional)
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Add to startup if selected
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startupicon

[Run]
; Option to run after install
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean up settings on uninstall (optional - commented out to preserve settings)
; Type: filesandordirs; Name: "{userappdata}\Battify"

[Code]
// Close running instance before uninstall/upgrade
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  // Try to close running Battify instance gracefully
  if Exec('taskkill', '/f /im Battify.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Sleep(500); // Give it time to close
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  // Close running instance before uninstall
  if Exec('taskkill', '/f /im Battify.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Sleep(500);
  end;
end;




