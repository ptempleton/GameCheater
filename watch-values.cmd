@echo off
REM ============================================================================
REM  Watch for VALUE cheats (money / fuel / spare tires / time of day).
REM  Usage:   watch-values SnowRunner int
REM           watch-values <ProcessName> <int|float|long|short|byte|double> [GameName]
REM  Run this from an ADMINISTRATOR terminal (needed to read the game's memory).
REM ============================================================================
cd /d "%~dp0"
dotnet run --project src/GameCheater.Demo -- --watch-values %*
