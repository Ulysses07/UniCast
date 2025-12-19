@echo off
REM UniCast Build & Publish Script
REM Bu script tek komutla installer oluşturur

echo ==========================================
echo    UniCast Build Script
echo ==========================================
echo.

REM Versiyon bilgisi
set VERSION=1.0.0
set OUTPUT_DIR=installer

REM 1. Temizlik
echo [1/5] Temizlik yapiliyor...
if exist publish rmdir /s /q publish
if exist %OUTPUT_DIR% rmdir /s /q %OUTPUT_DIR%
mkdir %OUTPUT_DIR%

REM 2. Release build
echo [2/5] Release build yapiliyor...
dotnet publish UniCast.App/UniCast.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
if errorlevel 1 (
    echo HATA: Build basarisiz!
    pause
    exit /b 1
)

REM 3. FFmpeg kopyala
echo [3/5] FFmpeg kopyalaniyor...
if exist "UniCast.App\External\ffmpeg.exe" (
    copy "UniCast.App\External\ffmpeg.exe" "publish\" >nul
    copy "UniCast.App\External\ffprobe.exe" "publish\" >nul
    echo    FFmpeg kopyalandi.
) else (
    echo    UYARI: FFmpeg bulunamadi! External klasorune ekleyin.
)

REM 4. Inno Setup ile installer oluştur
echo [4/5] Installer olusturuluyor...
where iscc >nul 2>&1
if errorlevel 1 (
    echo    UYARI: Inno Setup bulunamadi!
    echo    https://jrsoftware.org/isdl.php adresinden indirin.
    echo    Installer olusturulamadi, publish klasoru hazir.
) else (
    iscc UniCast.iss
    if errorlevel 1 (
        echo    HATA: Installer olusturulamadi!
    ) else (
        echo    Installer olusturuldu: %OUTPUT_DIR%\UniCast-Setup-%VERSION%.exe
    )
)

REM 5. SHA256 hash hesapla
echo [5/5] SHA256 hash hesaplaniyor...
if exist "%OUTPUT_DIR%\UniCast-Setup-%VERSION%.exe" (
    certutil -hashfile "%OUTPUT_DIR%\UniCast-Setup-%VERSION%.exe" SHA256 > "%OUTPUT_DIR%\sha256.txt"
    echo    Hash: %OUTPUT_DIR%\sha256.txt
)

echo.
echo ==========================================
echo    Build Tamamlandi!
echo ==========================================
echo.
echo Sonraki adimlar:
echo 1. %OUTPUT_DIR%\UniCast-Setup-%VERSION%.exe dosyasini unicastapp.com/downloads/ klasorune yukleyin
echo 2. website/downloads/update.json dosyasini guncelleyin
echo 3. SHA256 hash'i update.json'a ekleyin
echo.
pause
