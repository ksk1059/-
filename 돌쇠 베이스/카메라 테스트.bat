@echo off
cd /d "%~dp0"
set MECABRC=C:\mecabkodic\mecabrc

REM Camera diagnostic: shows live feed + detections in a window.
REM Console stays open so you can read OK/FAIL messages. q or ESC to quit.
"C:\dolswe_env\Scripts\python.exe" -m dolswe.camera_test
pause
