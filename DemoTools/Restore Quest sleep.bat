@echo off
REM Lets the Quest sleep again when taken off (undoes the proximity override from the mirror script).
cd /d "%~dp0scrcpy-win64-v5.0.1"
adb shell am broadcast -a com.oculus.vrpowermanager.automation_disable
