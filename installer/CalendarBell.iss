; CalendarBell per-user installer (Inno Setup 6). Built by scripts/publish.ps1 when ISCC.exe is available:
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\dist\CalendarBell-win-x64 /O..\dist installer\CalendarBell.iss
; No administrator rights: installs to %LOCALAPPDATA%\Programs\CalendarBell. User data in %LOCALAPPDATA%\CalendarBell
; is kept on update; uninstall asks whether to delete it.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\CalendarBell-win-x64"
#endif

[Setup]
AppId={{6C1F3E2A-5B7D-4C8E-9A10-CA1E0DABE11A}
AppName=CalendarBell
AppVersion={#AppVersion}
AppVerName=CalendarBell {#AppVersion}
AppPublisher=CalendarBell
DefaultDirName={localappdata}\Programs\CalendarBell
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputBaseFilename=CalendarBell-Setup
SetupIconFile=..\src\CalendarBell.App\Resources\Icons\calendarbell.ico
UninstallDisplayIcon={app}\CalendarBell.exe
UninstallDisplayName=CalendarBell
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "CalendarBell.dev.json"

[Icons]
Name: "{userprograms}\CalendarBell"; Filename: "{app}\CalendarBell.exe"; Parameters: "--settings"; Comment: "CalendarBell – meeting reminders"

[Run]
; Start the resident app (it registers "Start with Windows" itself, honoring the user's setting).
Filename: "{app}\CalendarBell.exe"; Flags: nowait postinstall skipifsilent; Description: "Start CalendarBell"

[UninstallRun]
; Stops the running instance, removes the sign-in task/Run key and the notification registration.
Filename: "{app}\CalendarBell.exe"; Parameters: "--uninstall --quiet --keep-files"; Flags: runhidden waituntilterminated; RunOnceId: "CalendarBellCleanup"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Also delete your CalendarBell settings, encrypted calendar URLs and logs?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\CalendarBell'), True, True, True);
  end;
end;
