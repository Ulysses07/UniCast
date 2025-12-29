; UniCast Installer Script
; Inno Setup 6.x için
; https://jrsoftware.org/isinfo.php adresinden ücretsiz indir

#define MyAppName "UniCast"
#define MyAppVersion "1.0.2"
#define MyAppPublisher "UniCast"
#define MyAppURL "https://unicastapp.com"
#define MyAppExeName "UniCast.App.exe"

[Setup]
; Uygulama bilgileri
AppId={{8F4E3B2A-1C5D-4E6F-9A8B-7C2D1E0F3A4B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Kurulum dizini
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Çıktı ayarları
OutputDir=installer
OutputBaseFilename=UniCast-Setup-{#MyAppVersion}
SetupIconFile=UniCast.App\Resources\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Windows versiyonu
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Görünüm
WizardStyle=modern
WizardSizePercent=120
DisableWelcomePage=no

; Yetki
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Uninstaller
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstü kısayolu oluştur"; GroupDescription: "Ek görevler:"
Name: "startupicon"; Description: "Windows başlangıcında çalıştır"; GroupDescription: "Ek görevler:"; Flags: unchecked

[Files]
; Ana uygulama dosyaları (publish klasöründen)
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; FFmpeg (External klasöründen)
Source: "UniCast.App\External\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Başlat menüsü
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} Kaldır"; Filename: "{uninstallexe}"

; Masaüstü (opsiyonel)
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

; Başlangıç (opsiyonel)
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
; Kurulum sonrası çalıştır
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} uygulamasını başlat"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Kaldırırken silinecek ek dosyalar
Type: filesandordirs; Name: "{app}\Logs"
Type: filesandordirs; Name: "{localappdata}\UniCast"

[Code]
// Önceki sürüm varsa kapat
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  
  // Çalışan UniCast varsa kapat
  if Exec('taskkill', '/F /IM UniCast.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Sleep(1000); // Kapanmasını bekle
  end;
end;

// Kurulum tamamlandığında
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // İsteğe bağlı: Kurulum sonrası işlemler
  end;
end;
