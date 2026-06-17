; Inno Setup — Odin Flash v1.0.1 (.NET Framework 4.8)
; Requiere: publish\release\ generado con Build-Production.ps1
; Compilar: ISCC.exe tools\OdinFlash.iss
;   o: powershell -ExecutionPolicy Bypass -File tools\Build-Production.ps1 -Obfuscate -Zip -Installer

#define MyAppVersion "1.0.1"
#define MyAppName "Odin Flash"
#define MyAppNameFull "Odin Flash v" + MyAppVersion
#define MyAppPublisher "Odin Flash"
#define MyAppExeName "Odin_Flash.exe"
#define PublishDir "..\publish\release"
#define OutputDir "..\publish\installer"

[Setup]
AppId={{E4F8A2B1-9C3D-4E7F-A1B2-5061724F4C41}
AppName={#MyAppNameFull}
AppVersion={#MyAppVersion}
AppVerName={#MyAppNameFull}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=Odin_Flash_{#MyAppVersion}_Setup
SetupIconFile=..\Assets\icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
MinVersion=10.0
UninstallDisplayIcon={app}\icon.ico
VersionInfoVersion=1.0.1.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppNameFull} — flasher Samsung Download Mode (LOKE)
VersionInfoProductName={#MyAppNameFull}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"; Comment: "{#MyAppName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"; Comment: "{#MyAppName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent unchecked

[Messages]
english.BeveledLabel=
spanish.BeveledLabel=
spanish.WelcomeLabel1=Bienvenido al instalador de {#MyAppName}
spanish.WelcomeLabel2=Este asistente instalará {#MyAppNameFull} en tu equipo.%n%nRequisito: .NET Framework 4.8 (Windows 10/11).%n%nEl flash de firmware puede borrar datos o dejar el dispositivo inutilizable si se usa firmware incorrecto. Úsalo solo si sabes lo que haces.
spanish.FinishedLabel=La instalación ha finalizado. Pulsa Finalizar para cerrar el asistente.
spanish.FinishedLabelNoIcons=La instalación ha finalizado. Pulsa Finalizar para cerrar el asistente.
spanish.ClickFinish=Finalizar
