; Built only through Build-Installer.ps1 from a verified self-contained payload.
#ifndef ReleaseVersion
  #error ReleaseVersion is required
#endif
#ifndef VersionInfo
  #error VersionInfo is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef PayloadManifest
  #error PayloadManifest is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef SourceCommit
  #error SourceCommit is required
#endif

#define AppGuid "{5D969E16-7FC9-4F13-9030-43864C2F110C}"
#define AppExe "NyKurEdge.App.exe"
#define ManifestName ".nykur-edge-payload.sha256"

[Setup]
; Permanent identity: do not change this GUID for future versions.
AppId={{5D969E16-7FC9-4F13-9030-43864C2F110C}
AppName=NyKur Edge
AppVersion={#ReleaseVersion}
AppVerName=NyKur Edge {#ReleaseVersion}
AppPublisher=NyKur
AppPublisherURL=https://nykuredge.outerworldnwo.tr/
AppSupportURL=https://nykuredge.outerworldnwo.tr/guide/
AppUpdatesURL=https://nykuredge.outerworldnwo.tr/download/
AppContact=founder@nykuredge.outerworldnwo.tr
AppCopyright=Copyright (c) 2026 NyKur
DefaultDirName={localappdata}\Programs\NyKur Edge
DefaultGroupName=NyKur Edge
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
WizardStyle=modern dynamic windows11
WizardSizePercent=110
SetupIconFile={#PayloadDir}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=NyKur Edge
Uninstallable=yes
CreateUninstallRegKey=yes
AppMutex=Local\NyKurEdge.App.Running
SetupMutex=Local\NyKurEdge.Setup.Running
CloseApplications=no
RestartApplications=no
AllowCancelDuringInstall=no
OutputDir={#OutputDir}
OutputBaseFilename=NyKur-Edge-{#ReleaseVersion}-Setup-x64
VersionInfoVersion={#VersionInfo}
VersionInfoTextVersion={#VersionInfo}
VersionInfoProductVersion={#VersionInfo}
VersionInfoProductTextVersion={#ReleaseVersion}
VersionInfoDescription=NyKur Edge per-user Windows installer
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
DiskSpanning=no
UninstallLogMode=append

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; Runtime files are private to this application; never register them globally.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadManifest}"; DestDir: "{app}"; DestName: "{#ManifestName}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\NyKur Edge"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Comment: "Your ambient Windows desktop companion"
Name: "{userdesktop}\NyKur Edge"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Comment: "Your ambient Windows desktop companion"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "&Launch NyKur Edge"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Messages]
WelcomeLabel2=NyKur Edge stays at the edge of your desktop, ready when you need it.%n%nSetup installs this preview for your Windows account. The application and its runtimes are included; administrator access is not required.%n%nNotification integration and Launch with Windows require the packaged application and are unavailable in this installer preview.
FinishedLabel=NyKur Edge is installed.%n%nFind it in the Start Menu. Its notification-area icon opens Settings and Exit; Windows may place it under the hidden-icons arrow.%n%nYour preferences are kept when you update or uninstall.

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppGuid}_is1';
  RunningMutex = 'Local\NyKurEdge.App.Running';
  LifecycleMutex = 'Local\NyKurEdge.App.Lifecycle';
  LifecycleBusyMessage = 'Another NyKur Edge installation or launch is in progress. Wait for it to finish, then try again.';
  CloseAppMessage = 'NyKur Edge is running. Choose Exit from its notification-area icon, then run Setup or Uninstall again. No application has been closed automatically.';

type
  TPayloadEntry = record
    Hash: String;
    RelativePath: String;
  end;
  TPayloadEntries = array of TPayloadEntry;

var
  PreviousPayload: TPayloadEntries;
  IncomingPayload: TPayloadEntries;
  PayloadPrepared: Boolean;
  LifecycleHandle: THandle;
  LifecycleHeld: Boolean;

function CreateMutexW(Attributes: LongWord; InitialOwner: LongWord; const Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function ReleaseMutex(Handle: THandle): LongWord;
  external 'ReleaseMutex@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): LongWord;
  external 'CloseHandle@kernel32.dll stdcall';

procedure ReleaseLifecycleGate;
begin
  if LifecycleHeld then begin
    ReleaseMutex(LifecycleHandle);
    LifecycleHeld := False;
  end;
  if LifecycleHandle <> 0 then begin
    CloseHandle(LifecycleHandle);
    LifecycleHandle := 0;
  end;
end;

function AcquireLifecycleGate: Boolean;
var
  WaitResult: LongWord;
begin
  Result := LifecycleHeld;
  if Result then Exit;
  LifecycleHandle := CreateMutexW(0, 0, LifecycleMutex);
  if LifecycleHandle = 0 then Exit;
  WaitResult := WaitForSingleObject(LifecycleHandle, 0);
  // WAIT_OBJECT_0 and WAIT_ABANDONED both grant ownership to this thread.
  Result := (WaitResult = 0) or (WaitResult = $80);
  LifecycleHeld := Result;
  if not Result then ReleaseLifecycleGate;
end;

function GetFileAttributesW(const FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function ContainsReparsePoint(const FileName: String): Boolean;
var
  Cursor, Parent: String;
begin
  Result := False;
  Cursor := FileName;
  while Cursor <> '' do begin
    if (GetFileAttributesW(Cursor) <> $FFFFFFFF) and
       ((GetFileAttributesW(Cursor) and $400) <> 0) then begin
      Result := True;
      Exit;
    end;
    Parent := ExtractFileDir(Cursor);
    if Parent = Cursor then Exit;
    Cursor := Parent;
  end;
end;

function DowngradeError: String;
var
  InstalledVersion: String;
  InstalledPacked, IncomingPacked: Int64;
begin
  Result := '';
  if RegKeyExists(HKCU64, UninstallKey) then begin
    if not RegQueryStringValue(HKCU64, UninstallKey, 'VersionInfoVersion', InstalledVersion) then begin
      Result := 'Setup could not verify the installed NyKur Edge version. Uninstall it through Windows Settings before installing this preview. Your preferences will be kept.';
      Exit;
    end;
    if not StrToVersion(InstalledVersion, InstalledPacked) then begin
      Result := 'Setup could not verify the installed NyKur Edge version. Uninstall it through Windows Settings before installing this preview. Your preferences will be kept.';
      Exit;
    end;
    if not StrToVersion('{#VersionInfo}', IncomingPacked) then begin
      Result := 'This installer has invalid version information.';
      Exit;
    end;
    if ComparePackedVersion(InstalledPacked, IncomingPacked) > 0 then
      Result := 'A newer NyKur Edge version is already installed. Download the current installer from https://nykuredge.outerworldnwo.tr/download/. Your installed application has not been changed.';
  end;
end;

function InitializeSetup: Boolean;
var
  ErrorText: String;
begin
  Result := False;
  ErrorText := DowngradeError;
  if ErrorText <> '' then begin
    SuppressibleMsgBox(ErrorText, mbError, MB_OK, IDOK);
    Exit;
  end;
  // Silent installations must never wait for, close, or overwrite a running app.
  if WizardSilent and CheckForMutexes(RunningMutex) then begin
    Log(CloseAppMessage);
    SuppressibleMsgBox(CloseAppMessage, mbError, MB_OK, IDOK);
    Exit;
  end;
  Result := True;
end;

function SafeRelativePath(const Path: String): Boolean;
var
  Normalized: String;
  I: Integer;
  ComponentEnd: Boolean;
begin
  Normalized := Path;
  StringChangeEx(Normalized, '/', '\', True);
  Result := False;
  if Normalized = '' then Exit;
  Result := (Normalized = Trim(Normalized)) and
    (Normalized[1] <> '\') and (Pos(':', Normalized) = 0) and
    (Pos('*', Normalized) = 0) and (Pos('?', Normalized) = 0) and
    (Pos('"', Normalized) = 0) and
    (Pos('\..\', '\' + Normalized + '\') = 0) and
    (Pos('\.\', '\' + Normalized + '\') = 0);
  if not Result then Exit;
  // Win32 trims trailing periods/spaces from path components. Reject them
  // rather than allowing a modified manifest to disguise parent traversal.
  for I := 1 to Length(Normalized) do begin
    if Ord(Normalized[I]) < 32 then begin
      Result := False;
      Exit;
    end;
    if I = Length(Normalized) then ComponentEnd := True
    else ComponentEnd := Normalized[I + 1] = '\';
    if ComponentEnd then
      if (Normalized[I] = '.') or (Normalized[I] = ' ') or (Normalized[I] = '\') then begin
        Result := False;
        Exit;
      end;
  end;
end;

function ReadPayloadManifest(const FileName: String; var Entries: TPayloadEntries): Boolean;
var
  Lines: TArrayOfString;
  I, J: Integer;
  Digest, RelativePath: String;
begin
  Result := False;
  if not LoadStringsFromFile(FileName, Lines) or (GetArrayLength(Lines) = 0) then Exit;
  SetArrayLength(Entries, GetArrayLength(Lines));
  for I := 0 to GetArrayLength(Lines) - 1 do begin
    if Length(Lines[I]) < 66 then Exit;
    if Lines[I][65] <> '|' then Exit;
    Digest := LowerCase(Copy(Lines[I], 1, 64));
    for J := 1 to Length(Digest) do
      if Pos(Digest[J], '0123456789abcdef') = 0 then Exit;
    RelativePath := Copy(Lines[I], 66, Length(Lines[I]));
    if not SafeRelativePath(RelativePath) then Exit;
    StringChangeEx(RelativePath, '/', '\', True);
    Entries[I].Hash := Digest;
    Entries[I].RelativePath := RelativePath;
  end;
  Result := True;
end;

function IncomingContains(const RelativePath: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(IncomingPayload) - 1 do
    if LowerCase(IncomingPayload[I].RelativePath) = LowerCase(RelativePath) then begin
      Result := True;
      Exit;
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  PreviousManifest: String;
begin
  Result := '';
  PayloadPrepared := False;
  if not AcquireLifecycleGate then begin
    Result := LifecycleBusyMessage;
    Exit;
  end;
  try
    try
      Result := DowngradeError;
      if Result <> '' then Exit;
      if CheckForMutexes(RunningMutex) then begin
        Result := CloseAppMessage;
        Exit;
      end;
      // Validate ownership manifests while the application launch gate is held.
      if ContainsReparsePoint(ExpandConstant('{app}')) then begin
        Result := 'Setup cannot install into a redirected application folder. Choose a normal local folder and retry.';
        Exit;
      end;
      ExtractTemporaryFile('{#ManifestName}');
      if not ReadPayloadManifest(ExpandConstant('{tmp}\{#ManifestName}'), IncomingPayload) then begin
        Result := 'The application payload manifest is invalid. Download the installer again.';
        Exit;
      end;
      PreviousManifest := ExpandConstant('{app}\{#ManifestName}');
      SetArrayLength(PreviousPayload, 0);
      if FileExists(PreviousManifest) then
        if not ReadPayloadManifest(PreviousManifest, PreviousPayload) then begin
          Result := 'The previous installation manifest is invalid. Uninstall NyKur Edge through Windows Settings before reinstalling. Your preferences will be kept.';
          Exit;
        end;
      PayloadPrepared := True;
    except
      Result := 'Setup could not prepare the application payload: ' + GetExceptionMessage;
    end;
  finally
    // Preparation failures release the gate so the wizard can safely retry.
    if not PayloadPrepared then ReleaseLifecycleGate;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
  RetiredFile: String;
  CleanupWarnings: Integer;
begin
  if (CurStep = ssInstall) and PayloadPrepared then begin
    if not LifecycleHeld then RaiseException(LifecycleBusyMessage);
    if CheckForMutexes(RunningMutex) then RaiseException(CloseAppMessage);
  end;
  if (CurStep = ssPostInstall) and PayloadPrepared then begin
    CleanupWarnings := 0;
    try
      // Inno recreates its uninstall key after processing [Registry] entries.
      // Persist release ordering/provenance only after that registration exists.
      if not RegWriteStringValue(HKCU64, UninstallKey, 'VersionInfoVersion', '{#VersionInfo}') then
        RaiseException('NyKur Edge files were installed, but Setup could not record the installed version. Uninstall through Windows Settings, then run this installer again. Your preferences will be kept.');
      if not RegWriteStringValue(HKCU64, UninstallKey, 'SourceCommit', '{#SourceCommit}') then
        RaiseException('NyKur Edge files were installed, but Setup could not record their source identity. Reinstall this preview before another update.');
      // Delete retired files only after all new files and registry entries have
      // installed successfully. Failed copying can still roll back the old app.
      for I := 0 to GetArrayLength(PreviousPayload) - 1 do
        if not IncomingContains(PreviousPayload[I].RelativePath) then begin
          RetiredFile := AddBackslash(ExpandConstant('{app}')) + PreviousPayload[I].RelativePath;
          try
            if FileExists(RetiredFile) then begin
              if ContainsReparsePoint(RetiredFile) then begin
                Log('Kept redirected retired file: ' + PreviousPayload[I].RelativePath);
                CleanupWarnings := CleanupWarnings + 1;
              end else if LowerCase(GetSHA256OfFile(RetiredFile)) = PreviousPayload[I].Hash then begin
                if DeleteFile(RetiredFile) then
                  Log('Removed retired owned payload file: ' + PreviousPayload[I].RelativePath)
                else begin
                  Log('Kept locked retired file after successful installation: ' + PreviousPayload[I].RelativePath);
                  CleanupWarnings := CleanupWarnings + 1;
                end;
              end else
                Log('Kept modified retired file: ' + PreviousPayload[I].RelativePath);
            end;
          except
            // Do not imply that successful installation can be rolled back
            // merely because optional obsolete-file cleanup could not finish.
            Log('Retired-file cleanup warning: ' + PreviousPayload[I].RelativePath + ': ' + GetExceptionMessage);
            CleanupWarnings := CleanupWarnings + 1;
          end;
        end;
    finally
      // The post-install Launch action must run after the mutation gate opens.
      ReleaseLifecycleGate;
    end;
    if CleanupWarnings > 0 then
      SuppressibleMsgBox('NyKur Edge was installed. Some obsolete application files could not be removed and were kept.', mbInformation, MB_OK, IDOK);
  end;
end;

procedure DeinitializeSetup;
begin
  ReleaseLifecycleGate;
end;

function InitializeUninstall: Boolean;
begin
  Result := AcquireLifecycleGate;
  if not Result then begin
    Log(LifecycleBusyMessage);
    SuppressibleMsgBox(LifecycleBusyMessage, mbError, MB_OK, IDOK);
    Exit;
  end;
  Result := not CheckForMutexes(RunningMutex);
  if not Result then begin
    Log(CloseAppMessage);
    SuppressibleMsgBox(CloseAppMessage, mbError, MB_OK, IDOK);
    ReleaseLifecycleGate;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then begin
    if not LifecycleHeld then RaiseException(LifecycleBusyMessage);
    if CheckForMutexes(RunningMutex) then RaiseException(CloseAppMessage);
  end;
end;

procedure DeinitializeUninstall;
begin
  ReleaseLifecycleGate;
end;
