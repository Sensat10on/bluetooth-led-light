@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem ============================================================================
rem  OpenTuya Sync -- remove the "downloaded from the internet" mark.
rem
rem  Why: Windows marks files downloaded from the internet and then warns about
rem  each program: "Windows protected your PC", publisher "Unknown publisher".
rem  The mark is removed once for all files, after which the warnings stop.
rem
rem  Run by double click from the program folder.
rem
rem  Text here is ASCII only -- see the note in НАСТРОИТЬ.bat.
rem ============================================================================

setlocal

echo ============================================================
echo   Removing the "downloaded from the internet" mark
echo ============================================================
echo.
echo Folder: %~dp0
echo.
echo Windows warns about these programs because they are signed with a
echo self-signed certificate, and such a signature is not enough for the
echo system to treat the program as known. Removing the mark stops the
echo warnings.
echo.

rem The command is written on one line: a line break made with "^" inside a
rem powershell call is parsed unreliably.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$root = '%~dp0'; $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue); $blocked = 0; foreach ($f in $files) { if (Get-Item -LiteralPath $f.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue) { $blocked++ } }; foreach ($f in $files) { Unblock-File -LiteralPath $f.FullName -ErrorAction SilentlyContinue }; Write-Host ('  files total:   ' + $files.Count); Write-Host ('  were marked:   ' + $blocked)"

echo.
echo ============================================================
echo   Done.
echo ============================================================
echo.
echo Now run the setup script (the file named NASTROIT) -- there should be
echo no warnings.
echo.
echo If a warning still appears, press "More info" in it and then
echo "Run anyway". Once is enough.
echo.
pause
