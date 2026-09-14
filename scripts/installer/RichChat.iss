#ifndef AppVersion
    #error AppVersion is required
#endif
#ifndef PublishRoot
    #error PublishRoot is required
#endif
#ifndef OutputRoot
    #error OutputRoot is required
#endif

[Setup]
AppId={{6C126AD0-C891-453D-9C87-9F5A70B19E35}
AppName=RichChat
AppVersion={#AppVersion}
AppPublisher=Dailin521
AppPublisherURL=https://github.com/Dailin521/relaycove
DefaultDirName={localappdata}\Programs\RichChat
DefaultGroupName=RichChat
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
SetupIconFile={#AddBackslash(SourcePath)}..\..\src\RelayCove.App\Resources\AppIcon\RelayCove.ico
UninstallDisplayIcon={app}\RichChat.exe
LicenseFile={#PublishRoot}\LICENSE
OutputDir={#OutputRoot}
OutputBaseFilename=RichChat-{#AppVersion}-win-x64-Setup
VersionInfoVersion={#AppVersion}.0
Compression=lzma2
SolidCompression=yes
; Older tray-resident builds may keep running after the normal close request.
; Force shutdown is scoped by Restart Manager to the target executable below.
CloseApplications=force
CloseApplicationsFilter=RichChat.exe
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\RichChat"; Filename: "{app}\RichChat.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\RichChat-R.ico"
Name: "{autodesktop}\RichChat"; Filename: "{app}\RichChat.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\RichChat-R.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\RichChat.exe"; Description: "{cm:LaunchProgram,RichChat}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent unchecked
