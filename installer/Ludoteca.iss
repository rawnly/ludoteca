; Installer di Ludoteca (Inno Setup 6).
; Si compila con installer\build.ps1, che prima pubblica l'app in ..\publish.

#define AppName "Ludoteca"
#define AppExe "Ludoteca.exe"
#define AppVersion GetVersionNumbersString("..\publish\Ludoteca.exe")

[Setup]
AppId={{6C1F3B7E-4A2D-4E8B-9C55-2B7D1E0A9F41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
; Installazione per l'utente corrente: niente richiesta di amministratore.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile=..\Assets\Ludoteca.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=Ludoteca-Setup-{#AppVersion}
; Chiude Ludoteca se è aperta durante l'aggiornamento o la disinstallazione.
CloseApplications=yes

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Prima di copiare i nuovi file si svuota la cartella, così un aggiornamento non lascia file vecchi.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
