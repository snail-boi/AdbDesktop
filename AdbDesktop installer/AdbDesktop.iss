; AdbDesktop installer.
;
; Built automatically by the AdbDesktop project on a Release build -- see the
; PackageRelease target in AdbDesktop.csproj. It can also be compiled by hand:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" "AdbDesktop.iss"
; which requires AdbDesktop to have been built in Release first.

#define MyAppExeName "AdbDesktop.exe"
#define BinDir "..\AdbDesktop\bin\Release\net10.0-windows"
#define MyAppVersion GetVersionNumbersString(SourcePath + "\" + BinDir + "\" + MyAppExeName)
#define MyAppName "adbDesktop"
#define MyAppPublisher "Snail"

[Setup]
; Distinct from AMPL's and ASM's AppIds: they are separate products that happen to
; share a publisher folder, and a shared id would make them uninstall each other.
AppId={{7F3C1A62-9D48-4B7E-A1C5-3E9B0D6F2A14}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=output
OutputBaseFilename=AdbDesktop_Setup
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";   Description: "Create a &desktop shortcut";    GroupDescription: "Additional shortcuts:"
Name: "startmenuicon"; Description: "Create a &Start Menu shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
; Managed output. Wildcarded rather than listed file by file: the dependency set
; changes with the NuGet references, and a missed entry would only show up as a
; runtime FileNotFoundException on someone else's machine.
Source: "{#BinDir}\{#MyAppExeName}";              DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\*.dll";                        DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\AdbDesktop.deps.json";            DestDir: "{app}"; Flags: ignoreversion
Source: "{#BinDir}\AdbDesktop.runtimeconfig.json";   DestDir: "{app}"; Flags: ignoreversion

; Native payload: adb, the scrcpy ports, FFmpeg, libwebp and the device-side server.
;
; Deliberately installed NEXT TO THE EXE, not into {userappdata}\Snail\Assets the way
; AMPL does. That folder is shared with AMPL and ASM, and all three drive one adb
; server -- overwriting their adb.exe with a different build makes every one of them
; flake at once. AppPaths.ResourceRoot resolves here for exactly this reason.
Source: "{#BinDir}\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs

; Licence notices ship with the binaries.
Source: "{#BinDir}\THIRD_PARTY_LICENSES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE.txt";                     DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";         Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; AdbDesktop's own data only. {userappdata}\Snail itself is left alone unless it ends up
; empty, because AMPL and ASM live there too.
Type: filesandordirs; Name: "{userappdata}\Snail\AdbDesktop"
Type: dirifempty;     Name: "{userappdata}\Snail"

[Registry]
Root: HKCU; Subkey: "Software\AdbDesktop"; ValueType: dword; ValueName: "Installed"; ValueData: "1"; Flags: uninsdeletekey

[Code]
// An installed copy must never be portable: portable.mode next to the exe would send
// config and icons into Program Files, which a standard user cannot write to. It can
// only get here if someone installs over a portable extraction.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    DeleteFile(ExpandConstant('{app}\portable.mode'));
end;
