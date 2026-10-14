; Echo.iss: one-file installer (Echo-Setup.exe), built by publish.ps1 with Inno Setup 6:
;   ISCC.exe /DAppVer=5.8.1 /DStageDir=<publish folder> /DOutDir=<output folder> deploy\Echo.iss
;
; Per-user, no admin: files go to %LOCALAPPDATA%\Programs\Echo, the folder the updater and
; UninstallRegistry already expect. The installer only copies files, adds the Start menu
; shortcut and launches Echo. It registers no uninstaller of its own: Echo writes its
; "Installed apps" entry at every startup (UninstallRegistry, uninstall.ps1), so the entry
; stays right after silent auto-updates. Settings and recordings are never touched.

#ifndef AppVer
  #error Pass /DAppVer=<version>
#endif
#ifndef StageDir
  #error Pass /DStageDir=<publish folder>
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
AppId={{3C12E6AF-6A36-4093-AFFC-1A12CABE8DBA}
AppName=Echo
AppVersion={#AppVer}
AppVerName=Echo {#AppVer}
AppPublisher=Echo
AppPublisherURL=https://github.com/romainio/echo-recorder
AppSupportURL=https://github.com/romainio/echo-recorder/issues
VersionInfoVersion={#AppVer}
VersionInfoDescription=Echo setup
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Echo
DisableDirPage=yes
DisableProgramGroupPage=yes
Uninstallable=no
CloseApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
SetupIconFile=..\src\Echo.App\Assets\echo.ico
OutputDir={#OutDir}
OutputBaseFilename=Echo-Setup
Compression=lzma2/ultra64
SolidCompression=yes
ShowLanguageDialog=auto

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Messages]
french.FinishedLabel=Echo est installé.%n%nAu premier lancement, ajoutez une clé API dans Paramètres. Le bouton Aide explique comment en obtenir une gratuitement.
english.FinishedLabel=Echo is installed.%n%nOn first launch, add an API key in Settings. The Help button explains how to get one for free.
spanish.FinishedLabel=Echo está instalado.%n%nEn el primer inicio, añade una clave API en Ajustes. El botón Ayuda explica cómo obtener una gratis.

[CustomMessages]
french.EchoRunning=Echo est ouvert. Quittez-le (clic droit sur son icône près de l'horloge, puis Quitter), puis cliquez sur OK.%n%nSi une réunion est en cours d'enregistrement, attendez qu'elle soit terminée.
english.EchoRunning=Echo is running. Quit it (right-click its icon near the clock, then Quit), then click OK.%n%nIf a meeting is being recorded, wait until it ends.
spanish.EchoRunning=Echo está abierto. Ciérralo (clic derecho en su icono junto al reloj y luego Salir) y haz clic en Aceptar.%n%nSi se está grabando una reunión, espera a que termine.

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Echo"; Filename: "{app}\Echo.exe"; WorkingDir: "{app}"; Comment: "Echo"

[Run]
Filename: "{app}\Echo.exe"; Description: "{cm:LaunchProgram,Echo}"; Flags: nowait postinstall skipifsilent

[Code]
// Echo is never closed for the user: a meeting may be recording. Setup waits until they quit it.
function EchoRunning(): Boolean;
var
  rc: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'),
    '/C tasklist /FI "IMAGENAME eq Echo.exe" /NH | find /I "Echo.exe" >NUL',
    '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  while EchoRunning() do
    if SuppressibleMsgBox(CustomMessage('EchoRunning'), mbInformation, MB_OKCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := CustomMessage('EchoRunning');
      Exit;
    end;
end;
