@echo off
REM Installs (or updates) Builds\BDCity.apk on the connected Quest and starts it.
cd /d "%~dp0scrcpy-win64-v5.0.1"
adb install -r "%~dp0..\Builds\BDCity.apk" && adb shell monkey -p nl.hr.bdcity -c android.intent.category.LAUNCHER 1
pause
