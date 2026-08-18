@echo off
rem Thin launcher so this can still be double-clicked from Explorer; real logic is in run.ps1.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0/PowerShell/run.ps1"
