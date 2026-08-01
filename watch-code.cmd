@echo off
REM ============================================================================
REM  Watch for CODE cheats (God Mode / No Damage / No Reload).
REM  Usage:   watch-code SnowRunner
REM           watch-code <ProcessName> [GameName]
REM  Run this from an ADMINISTRATOR terminal (needed to read the game's memory).
REM ============================================================================
cd /d "%~dp0"
dotnet run --project src/GameCheater.Demo -- --watch-code %*
