; Modern Download Manager pre-alpha installer
; Build the staged files first with: .\Build-Release.ps1

#define MyAppName "Modern Download Manager"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "Modern Download Manager"
#define MyAppExeName "ModernDownloadManager.App.exe"
#define StageDir "..\artifacts\ModernDownloadManager-prealpha-0.1.0"

[Setup]
AppId={{A8C8D00F-0A53-4F8B-9F98-9A5DDBE5B9B7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion} (pre-alpha)
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\ModernDownloadManager
DefaultGroupName={#MyAppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=ModernDownloadManager-setup-{#MyAppVersion}-prealpha
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
UninstallDisplayName={#MyAppName} {#MyAppVersion} (pre-alpha)

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#StageDir}\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\extension\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\extension-firefox\*"; DestDir: "{app}\extension-firefox"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\native-host-setup\*"; DestDir: "{app}\native-host-setup"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\app\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\app\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\native-host-setup\Install-NativeHost.ps1"" -NativeHostExePath ""{app}\app\ModernDownloadManager.NativeHost.exe"""; Description: "Register browser integration (Chrome and Edge)"; Flags: runhidden waituntilterminated
Filename: "explorer.exe"; Parameters: """{app}\extension"""; Description: "Open the extension folder for browser setup"; Flags: postinstall shellexec skipifsilent
Filename: "{app}\app\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\native-host-setup\Uninstall-NativeHost.ps1"""; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{app}\extension"
Type: filesandordirs; Name: "{app}\extension-firefox"
Type: filesandordirs; Name: "{app}\native-host-setup"
