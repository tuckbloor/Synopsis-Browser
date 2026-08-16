@echo off
setlocal EnableExtensions
set "ROOT=%~dp0"
set "DOTNET_NOLOGO=1"
title Synopsis Browser for Developers

where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo Synopsis needs the .NET 8 SDK to run from source.
  echo Download/install .NET 8 SDK, then double-click OPEN-SYNOPSIS.cmd again.
  echo.
  pause
  exit /b 1
)

if not exist "%ROOT%src\SynopsisBrowser.App\SynopsisBrowser.App.csproj" (
  echo.
  echo Synopsis could not find its application project.
  echo Fully extract the repository/ZIP and run OPEN-SYNOPSIS.cmd from the project root.
  echo.
  pause
  exit /b 1
)

cd /d "%ROOT%"
dotnet run --project "%ROOT%src\SynopsisBrowser.App\SynopsisBrowser.App.csproj"
if errorlevel 1 (
  echo.
  echo Synopsis did not start. The compiler/runtime error is shown above.
  echo.
  pause
  exit /b 1
)
