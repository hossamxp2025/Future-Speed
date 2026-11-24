; ---------------------------------------------------------------------------
;  Future Speed File Transfer - Inno Setup script
;
;  Build the payload first:
;      powershell -ExecutionPolicy Bypass -File build\publish.ps1
;  Then compile this script with Inno Setup 6 (free, open source):
;      "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\FutureSpeed.iss
;  The installer lands in artifacts\installer\.
; ---------------------------------------------------------------------------

#define AppName        "Future Speed File Transfer"
#define AppShortName   "Future Speed"
#define AppVersion     "1.1.0"
#define AppPublisher   "Future Speed"
#define AppExeName     "FutureSpeed.exe"
#define SourceDir      "..\artifacts\publish\win-x64"

[Setup]
AppId={{E4C1D4B2-9F3A-4C77-9F1D-8B2A5C7E31A6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppShortName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=..\artifacts\installer
OutputBaseFilename=FutureSpeed-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-user installs need no elevation; the user can still pick Program Files.
; "commandline" also allows an unattended per-user install: setup.exe /VERYSILENT /CURRENTUSER
PrivilegesRequiredOverridesAllowed=commandline dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
LicenseFile=
SetupIconFile=..\src\FutureSpeed.App\Assets\FutureSpeed.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic";  MessagesFile: "compiler:Languages\Arabic.isl"

[CustomMessages]
english.ExplorerIntegration=Add "Copy / Move / Paste with Future Speed" to the File Explorer menu
english.IntegrationGroup=File Explorer integration
arabic.ExplorerIntegration=إضافة أوامر «نسخ / نقل / لصق بواسطة Future Speed» إلى قائمة مستكشف الملفات
arabic.IntegrationGroup=تكامل مستكشف الملفات

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Opt-in: the keys are written under HKCU by the app itself, so no elevation is needed and
; the user can turn it off again from Settings at any time.
Name: "explorerintegration"; Description: "{cm:ExplorerIntegration}"; GroupDescription: "{cm:IntegrationGroup}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppShortName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppShortName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppShortName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Registers the Explorer verbs for the installing user only (HKCU), when the task was ticked.
Filename: "{app}\{#AppExeName}"; Parameters: "--register-explorer"; Flags: runhidden waituntilterminated; Tasks: explorerintegration
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppShortName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Always remove the Explorer verbs, whether they were installed by the installer or later
; from the Settings page, so no registry key survives an uninstall.
Filename: "{app}\{#AppExeName}"; Parameters: "--unregister-explorer"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveExplorerIntegration"

[Registry]
; Belt and braces: even if the executable cannot run during uninstall, these keys go.
; "dontcreatekey" means nothing is written at install time; the app owns that.
Root: HKCU; Subkey: "Software\Classes\*\shell\FutureSpeed.Copy"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\FutureSpeed.Move"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\FutureSpeed.Copy"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\FutureSpeed.Move"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\FutureSpeed.Paste"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\FutureSpeed.Paste"; Flags: dontcreatekey uninsdeletekey

[UninstallDelete]
; Logs, reports and checkpoints live in %APPDATA%\FutureSpeed and are intentionally kept,
; so an uninstall never throws away a user's transfer history or an unfinished job.
Type: dirifempty; Name: "{app}"
