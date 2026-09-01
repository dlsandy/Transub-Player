@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ========================================
echo  Transub Player release pack
echo ========================================
echo.
echo Output: artifacts\pack\
echo   - *-win-x64-setup.exe   installer (needs Inno Setup 6)
echo   - *-win-x64.zip         portable
echo   - TransubPlayer-win-x64.zip  updater alias
echo.
echo Includes: self-contained win-x64 + mpv + embedded Whisper
echo (no ASR/MT model weights)
echo.
echo Optional args:
echo   -Target All^|Portable^|Setup
echo   -SkipNativePrep       use existing native\ only
echo   -FrameworkDependent   smaller; needs .NET Desktop Runtime
echo   -SkipZip              portable folder only, no zip
echo.
echo Examples:
echo   pack.bat
echo   pack.bat -Target Portable
echo   pack.bat -SkipNativePrep
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\pack-release.ps1" %*
set ERR=%ERRORLEVEL%
if not "%ERR%"=="0" (
  echo.
  echo Pack failed, exit code %ERR%
  echo.
  pause
  exit /b %ERR%
)

echo.
echo Pack finished. See artifacts\pack\
echo.
pause
exit /b 0
