@echo off
cd /d "%~dp0"
set MECABRC=C:\mecabkodic\mecabrc

REM Start Ollama server if not already running
tasklist /fi "imagename eq ollama.exe" 2>nul | find /i "ollama.exe" >nul
if errorlevel 1 (
    start "" /b ollama serve >nul 2>&1
    timeout /t 4 /nobreak >nul
)

REM Launch dolswe (no console window; errors go to dolswe.log)
"C:\dolswe_env\Scripts\pythonw.exe" -m dolswe.app 1>>"%~dp0dolswe.log" 2>&1
