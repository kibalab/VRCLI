#ifndef AppVersion
  #error AppVersion must be provided with /DAppVersion=x.y.z
#endif
#ifndef SourceDir
  #error SourceDir must be provided with /DSourceDir=path
#endif
#ifndef OutputDir
  #error OutputDir must be provided with /DOutputDir=path
#endif

[Setup]
AppId={{7D249809-6F83-45F2-9FC8-7C66788A32E6}
AppName=VRCLI
AppVersion={#AppVersion}
AppPublisher=KIBA_
AppPublisherURL=https://github.com/kibalab/VRCLI
AppSupportURL=https://github.com/kibalab/VRCLI/issues
AppUpdatesURL=https://github.com/kibalab/VRCLI/releases/latest
DefaultDirName={localappdata}\Programs\VRCLI
DefaultGroupName=VRCLI
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesEnvironment=yes
CloseApplications=yes
CloseApplicationsFilter=VRCLI.exe
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=VRCLI-{#AppVersion}-win-x64-setup
UninstallDisplayIcon={app}\VRCLI.exe
VersionInfoVersion={#AppVersion}
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "addtopath"; Description: "Add VRCLI to the user PATH"; GroupDescription: "Command line:"; Flags: checkedonce

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\VRCLI.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\VRCLI.exe"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\VRCLI.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\VRCLI.exe"; Parameters: "--help"; Description: "Open VRCLI help"; Flags: postinstall nowait skipifsilent unchecked

[Code]
const
  EnvironmentKey = 'Environment';
  ProductKey = 'Software\KibaLab\VRCLI';

function NormalizedPath(Value: string): string;
begin
  Result := Lowercase(RemoveBackslashUnlessRoot(Trim(Value)));
end;

function PathContains(PathValue, Entry: string): Boolean;
var
  Entries: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  Entries := SplitString(PathValue, ';');
  for Index := 0 to GetArrayLength(Entries) - 1 do
  begin
    if NormalizedPath(Entries[Index]) = NormalizedPath(Entry) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure AddToUserPath(Entry: string);
var
  PathValue: string;
begin
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', PathValue) then
    PathValue := '';

  if not PathContains(PathValue, Entry) then
  begin
    if (PathValue <> '') and (PathValue[Length(PathValue)] <> ';') then
      PathValue := PathValue + ';';
    RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', PathValue + Entry);
    RegWriteDWordValue(HKCU, ProductKey, 'PathAdded', 1);
  end;
end;

procedure RemoveFromUserPath(Entry: string);
var
  PathValue, NewValue: string;
  Entries: TArrayOfString;
  Index: Integer;
  Marker: Cardinal;
begin
  if not RegQueryDWordValue(HKCU, ProductKey, 'PathAdded', Marker) or (Marker <> 1) then
    Exit;
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', PathValue) then
    Exit;

  Entries := SplitString(PathValue, ';');
  NewValue := '';
  for Index := 0 to GetArrayLength(Entries) - 1 do
  begin
    if (Entries[Index] <> '') and (NormalizedPath(Entries[Index]) <> NormalizedPath(Entry)) then
    begin
      if NewValue <> '' then
        NewValue := NewValue + ';';
      NewValue := NewValue + Entries[Index];
    end;
  end;

  RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', NewValue);
  RegDeleteKeyIncludingSubkeys(HKCU, ProductKey);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToUserPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFromUserPath(ExpandConstant('{app}'));
end;
