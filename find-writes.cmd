@echo off
REM ============================================================================
REM  Find out WHAT WRITES to an address (the code-cheat finder).
REM  Use this when a value can't be frozen because the game recomputes it every
REM  frame — fuel, stamina, durability. Value-scan first to get an address, then:
REM
REM  Usage:   find-writes SnowRunner 1F3A40C20
REM           find-writes <ProcessName|pid> <HexAddress> [Size] [GameName]
REM
REM  Size defaults to 4 bytes (use 8 for doubles/int64, 1 for a byte).
REM  Run this from an ADMINISTRATOR terminal (attaching as a debugger needs it).
REM  Single-player only — never against an EAC/BattlEye-protected session.
REM ============================================================================
cd /d "%~dp0"
dotnet run --project src/GameCheater.Demo -- --find-writes %*
