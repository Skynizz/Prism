; Installeur Windows de Prism (Inno Setup 6).
;
; Compile par scripts\release.ps1 :
;   ISCC.exe /DAppVersion=1.2.0 /DPublishDir=<dossier publie> /DOutputDir=<sortie> installer\Prism.iss
;
; Installation par utilisateur, sans droits administrateur, dans %LOCALAPPDATA%\Programs\Prism :
; c'est ce qui permet a Prism de se mettre a jour lui-meme. Les donnees de l'utilisateur
; (%LOCALAPPDATA%\Prism : sauvegardes des jeux, registre, reglages) ne sont jamais supprimees
; a la desinstallation — elles servent a restaurer les jeux.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish-" + AppVersion
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

; Canal : vide pour la version stable, "dev" pour une build de test. Une build de test
; s'installe a cote de la stable, avec son propre identifiant de desinstallation et son
; propre dossier — elle ne remplace jamais la version que l'utilisateur garde.
#ifndef Channel
  #define Channel ""
#endif
#ifndef FullVersion
  #define FullVersion AppVersion
#endif

#if Channel == ""
  #define AppName "Prism"
  #define AppDir "Prism"
  #define AppGuid "{{8F3C2A71-5B4E-4D2A-9C61-7E1B3D5A9F24}"
#else
  #define AppName "Prism (" + Channel + ")"
  #define AppDir "Prism-" + Channel
  #define AppGuid "{{2D7B5E64-9C31-4A8F-B0D2-5E4C1A7F3B96}"
#endif

#define AppExe "Prism.exe"
#define AppUrl "https://github.com/Skynizz/Prism"

[Setup]
AppId={#AppGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#FullVersion}
AppPublisher=Skynizz
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=© 2026 Skynizz
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup

PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppDir}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

LicenseFile=..\LICENSE
SetupIconFile=..\Assets\Prism.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=..\Assets\installer\wizard.bmp,..\Assets\installer\wizard@2x.bmp
WizardSmallImageFile=..\Assets\installer\wizard-small.bmp,..\Assets\installer\wizard-small@2x.bmp

OutputDir={#OutputDir}
OutputBaseFilename=Prism-Setup-{#FullVersion}
Compression=lzma2/ultra64
SolidCompression=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Prism demande l'elevation (dossiers de jeux sous Program Files) : lancement via le shell.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
; Restes d'une mise a jour automatique.
Type: files; Name: "{app}\*.old"
