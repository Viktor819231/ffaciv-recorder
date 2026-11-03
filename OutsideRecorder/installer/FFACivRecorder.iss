; FFACiv Recorder installer (card OR04). Inno Setup 7, built by build-installer.sh (which passes AppVersion and the
; folder of the published program). Per-user: installs into %LOCALAPPDATA%\Programs\FFACiv Recorder, never asks for
; administrator, shows in Windows Settings > Apps, upgrades in place (same AppId), keeps the recordings on uninstall
; unless the player says to remove them. Public builds use RecorderSigned; unsigned builds are labelled tests.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\out\or04\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\out\or04"
#endif

#define AppExe "FFACivRecorder.exe"
#ifndef SetupFilename
  #define SetupFilename "FFACiv-Recorder-Setup-" + AppVersion + "-UNSIGNED-TEST"
#endif
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#ifndef TestInstall
  #define AppName "FFACiv Recorder"
  #define AppIdGuid "{{3687E8A9-4D31-439F-9929-AC72F71EE137}"
  ; the name the program itself uses for its Run entry (LiveBackend.StartWithWindows): one entry, two writers
  #define RunName "FFACiv Recorder"
  ; the program's single-instance mutex (Program.cs)
  #define AppMutexName "FFACivRecorderUI.Single"
  ; the recordings folder (settings.txt output_folder default) under %LOCALAPPDATA%
  #define DataSub "FFACiv\OutsideRecorder"
#else
  ; /DTestInstall: an installer for install/uninstall tests on a PC that has the real one installed and running:
  ; its own name, Apps entry, folder, Run entry and data folder, and it never closes the real recorder
  #define AppName "FFACiv Recorder TEST"
  #define AppIdGuid "{{3687E8A9-4D31-439F-9929-AC72F71EE138}"
  #define RunName "FFACiv Recorder TEST"
  #define AppMutexName "FFACivRecorderUI.InstallTest.NoSuchMutex"
  #define DataSub "FFACiv\OutsideRecorderInstallTest"
#endif

[Setup]
; never change AppId: Windows and later versions find this install by it (upgrade in place, one entry in Apps)
AppId={#AppIdGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=FFACiv
AppPublisherURL=https://ffaciv.com
AppSupportURL=https://ffaciv.com
AppCopyright=Copyright (c) 2026 FFACiv
VersionInfoCompany=FFACiv
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
#ifdef RecorderSigned
SignTool=RecorderSign
SignedUninstaller=yes
SignToolRunMinimized=yes
#endif
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright (c) 2026 FFACiv
; per-user, no administrator
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={userpf}\{#AppName}
UsePreviousAppDir=yes
; one folder for everyone: fewer pages for players (an upgrade uses the folder of the install it replaces)
DisableDirPage=yes
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableReadyPage=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupIconFile=..\OutsideRecorder.Shell\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard-1x.png,wizard-2x.png
WizardSmallImageFile=wizard-small-1x.png,wizard-small-2x.png
OutputDir={#OutputDir}
#ifdef TestInstall
OutputBaseFilename=FFACiv-Recorder-Setup-{#AppVersion}-INSTALLTEST
#else
OutputBaseFilename={#SetupFilename}
#endif
Compression=lzma2/max
SolidCompression=yes
; the program is closed by [Code] below (it may be hidden by the clock), not by Windows' Restart Manager
CloseApplications=no
RestartApplications=no
ChangesEnvironment=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This installs [name/ver] for you only (no administrator needed).%n%nThe introduction explains recording methods, optional experimental action logs, and when and how games are uploaded. Existing preferences are preserved.%n%nYour original Civ saves are left untouched. Uninstalling keeps recorder copies unless you choose to remove them.

[Tasks]
Name: "startup"; Description: "Start with Windows (waits by the clock, records when Civ starts)"; Flags: unchecked
Name: "desktopicon"; Description: "Make a desktop shortcut"; Flags: unchecked

[InstallDelete]
; an upgrade replaces the whole program, so files an older version had and this one does not cannot linger;
; settings.txt (the player's settings) is kept
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\*.exe"
Type: files; Name: "{app}\*.json"
Type: filesandordirs; Name: "{app}\ui"
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "settings.txt,*.pdb"

#ifdef TestInstall
; A test installer always carries an explicit test endpoint, preserving that install's later settings.
Source: "{#SourceDir}\settings.txt"; DestDir: "{app}"; Flags: onlyifdoesntexist
#endif

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Records your Civilization V games"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Records your Civilization V games"; Tasks: desktopicon

[Registry]
; start with Windows: the same entry the program's own Settings switch turns on and off
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#RunName}"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: startup
Root: HKCU; Subkey: "{#RunKey}"; ValueType: none; ValueName: "{#RunName}"; Flags: deletevalue; Tasks: not startup

[Run]
#ifndef TestInstall
Filename: "{app}\{#AppExe}"; Parameters: "--setup"; Description: "Start {#AppName} and open the introduction"; Flags: nowait postinstall skipifsilent
#else
; a test install never starts the program (the real one may be running: one recorder at a time)
Filename: "{app}\{#AppExe}"; Parameters: "--setup"; Description: "Start {#AppName} and open the introduction"; Flags: nowait postinstall skipifsilent unchecked
#endif

[UninstallDelete]
; the program's own files made after install: settings.txt next to it, and its window data (WebView2 cache,
; window place, window settings) — never the recordings
Type: files; Name: "{app}\settings.txt"
Type: dirifempty; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\{#DataSub}\app"

[Code]
const
  RecordingsSub = '{#DataSub}';

var
  RemoveRecordings: Boolean;

{ The recorder may run hidden by the clock: close it before files are replaced or removed. A hard stop loses
  nothing: the recording files are the queue and the next start continues them. }
function CloseRecorder(Ask: Boolean): Boolean;
var
  Code, Tries: Integer;
begin
  Result := True;
  if not CheckForMutexes('{#AppMutexName}') then Exit;
  if Ask and (SuppressibleMsgBox('FFACiv Recorder is running. It will be closed now.' + #13#10 + #13#10 +
      'Nothing is lost: an unfinished recording continues when the recorder starts again.',
      mbInformation, MB_OKCANCEL, IDOK) <> IDOK) then
  begin
    Result := False;
    Exit;
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Tries := 0;
  while CheckForMutexes('{#AppMutexName}') and (Tries < 50) do
  begin
    Sleep(100);
    Tries := Tries + 1;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not CloseRecorder(not WizardSilent) then
    Result := 'Please close FFACiv Recorder (right-click its ring icon by the clock, Quit), then try again.';
end;

#ifndef TestInstall
{ 0.7.18: the user build sends to ffaciv.com. A kept settings.txt from an earlier test install may still name the
  ffaciv-test site; those two lines are emptied (empty = ffaciv.com), everything else the player set is kept. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Path: String;
  Lines: TArrayOfString;
  I: Integer;
  Changed: Boolean;
begin
  if CurStep <> ssPostInstall then Exit;
  Path := ExpandConstant('{app}\settings.txt');
  if not LoadStringsFromFile(Path, Lines) then Exit;
  Changed := False;
  for I := 0 to GetArrayLength(Lines) - 1 do
    if (Pos('ffaciv-test.pages.dev', Lowercase(Lines[I])) > 0) and (Copy(Trim(Lines[I]), 1, 1) <> '#') then
    begin
      if Pos('stream_url', Trim(Lines[I])) = 1 then begin Lines[I] := 'stream_url ='; Changed := True; end
      else if Pos('live_site_url', Trim(Lines[I])) = 1 then begin Lines[I] := 'live_site_url ='; Changed := True; end;
    end;
  if Changed then SaveStringsToUTF8File(Path, Lines, False);
end;
#endif

function InitializeUninstall(): Boolean;
begin
  Result := CloseRecorder(not UninstallSilent);
  { /REMOVERECORDINGS on the command line: the same as answering "Yes" below (for silent uninstalls and tests) }
  RemoveRecordings := ExpandConstant('{param:REMOVERECORDINGS|0}') = '1';
end;

{ bytes in a folder and its subfolders }
function DirBytes(const Dir: String): Int64;
var
  F: TFindRec;
begin
  Result := 0;
  if FindFirst(AddBackslash(Dir) + '*', F) then
  try
    repeat
      if (F.Name <> '.') and (F.Name <> '..') then
      begin
        if F.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          Result := Result + DirBytes(AddBackslash(Dir) + F.Name)
        else
          Result := Result + Int64(F.SizeHigh) * 4294967296 + F.SizeLow;
      end;
    until not FindNext(F);
  finally
    FindClose(F);
  end;
end;

function SizeText(Bytes: Int64): String;
begin
  if Bytes < 1048576 then Result := 'less than 1 MB'
  else Result := 'about ' + IntToStr(Bytes div 1048576) + ' MB';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Recordings, RunValue: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { the Run entry goes only if it starts this install (a test build elsewhere keeps its own) }
    if RegQueryStringValue(HKCU, '{#RunKey}', '{#RunName}', RunValue) and
       (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(RunValue)) > 0) then
      RegDeleteValue(HKCU, '{#RunKey}', '{#RunName}');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    Recordings := ExpandConstant('{localappdata}\' + RecordingsSub);
    if DirExists(Recordings) and not RemoveRecordings and not UninstallSilent then
      RemoveRecordings := MsgBox('Also remove your recordings?' + #13#10 + #13#10 +
        'Your recorded games are kept in' + #13#10 + Recordings + #13#10 +
        '(' + SizeText(DirBytes(Recordings)) + '). Recordings already sent to ffaciv.com stay on the site.' + #13#10 + #13#10 +
        'Yes = remove them from this PC.   No = keep them (recommended).',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    if RemoveRecordings and DirExists(Recordings) then
      DelTree(Recordings, True, True, True);
    { leave no empty FFACiv folder behind }
    RemoveDir(ExpandConstant('{localappdata}\FFACiv'));
  end;
end;
