@echo off
REM B&D City - spectator view on this screen / projector, over the USB cable (no Wi-Fi, no Meta account).
REM 1. Plug the Quest in with USB-C (data cable) and start B&D City in the headset.
REM 2. Run this. A browser opens with the view. Double-click the picture (or press F11) for fullscreen.
cd /d "%~dp0scrcpy-win64-v5.0.1"

REM Keep the headset awake when it is taken off between visitors (undo: Restore Quest sleep.bat).
adb shell am broadcast -a com.oculus.vrpowermanager.prox_close >nul 2>&1

REM Route the game's stream (port 8090 in the headset) to this laptop.
adb forward tcp:8090 tcp:8090
if errorlevel 1 (
  echo.
  echo Could not reach the Quest. Is it plugged in, and did you allow USB debugging in the headset?
  pause
  exit /b 1
)
start "" "http://localhost:8090"
