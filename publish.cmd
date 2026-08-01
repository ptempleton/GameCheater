@echo off
REM ============================================================================
REM  Compile the GameCheater CLIENT into a single self-contained .exe.
REM  No .NET install needed to run the result.
REM  Output:  publish\GameCheater.exe   (run as Administrator to attach to games)
REM ============================================================================
cd /d "%~dp0"
dotnet publish src/GameCheater.App -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
echo.
echo Client built:  %~dp0publish\GameCheater.exe
