; Inno Setup 6 script for Ventrail. Normally run by build\package.ps1, which passes:
;   /DAppVersion=1.0.0  /DSourceDir=<published folder>
;
; Installs for the current user only (no admin prompt to install), into
; %LocalAppData%\Programs\Ventrail, with Start menu and optional desktop shortcuts.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\Ventrail-" + AppVersion + "-win-x64"
#endif

[Setup]
AppId={{6C1F2E8A-4B7D-4E39-9A51-0F3C7B2D8E14}
AppName=Ventrail
AppVersion={#AppVersion}
AppPublisher=Tan Lih Zhe (Sunway University, Capstone Project 2)
DefaultDirName={localappdata}\Programs\Ventrail
DefaultGroupName=Ventrail
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Ventrail-{#AppVersion}-Setup
SetupIconFile=..\Ventrail\Assets\Ventrail.ico
UninstallDisplayIcon={app}\Ventrail.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Ventrail"; Filename: "{app}\Ventrail.exe"
Name: "{group}\Ventrail (demo data)"; Filename: "{app}\Ventrail.exe"; Parameters: "--demo"
Name: "{group}\Ventrail user guide"; Filename: "{app}\USER_GUIDE.md"
Name: "{autodesktop}\Ventrail"; Filename: "{app}\Ventrail.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Ventrail.exe"; Description: "Start Ventrail"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove Ventrail's two firewall rules (only exist if peer-to-peer sharing was allowed).
; Windows asks for admin permission; saying No just leaves the two harmless rules behind.
Filename: "{cmd}"; Parameters: "/c netsh advfirewall firewall delete rule name=""Ventrail P2P (TCP 4001)"" & netsh advfirewall firewall delete rule name=""Ventrail P2P (UDP 4002)"""; \
  Flags: shellexec runhidden waituntilterminated; Verb: "runas"; RunOnceId: "RemoveFirewallRules"

; Your repositories, their history and %LocalAppData%\Ventrail (peer id, recent folders) are never
; removed by the uninstaller.
