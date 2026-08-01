@echo off
REM ============================================================================
REM  Launch the GameCheater UI (game picker, cheat toggles, Refresh).
REM  Usage:   app
REM  Run as ADMINISTRATOR if you want to attach to a game and toggle cheats.
REM ============================================================================
cd /d "%~dp0"
dotnet run --project src/GameCheater.App
