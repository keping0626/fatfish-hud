@echo off
chcp 65001 >nul
title SPT Quest and Hideout Checker
cd /d "%~dp0"

rem Prefer python on PATH; fall back to a portable python next to this script.
set "PY="
where python >nul 2>nul && set "PY=python"
if not defined PY if exist "%~dp0python\python.exe" set "PY=%~dp0python\python.exe"
if not defined PY (
    echo [ERROR] Python not found. Install Python 3.8+ and add it to PATH.
    pause
    exit /b 1
)

rem If SPT_Runtime is not above this folder, set it explicitly:
rem set "SPT_BASE=D:\SPT\SPT_Runtime"

"%PY%" quest_hideout_checker.py --pick
echo.
pause
