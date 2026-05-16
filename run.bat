@echo off
title Galgame Quote Collector
cd /d "%~dp0GalgameQuoteCollector"

:: check dotnet SDK
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] .NET SDK 8 not found. Please install from:
    echo https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

:: kill lingering process
taskkill /F /IM GalgameQuoteCollector.exe 2>nul >nul

:: build and run
dotnet run --no-build 2>nul
if %errorlevel% neq 0 (
    echo [BUILD] First launch, compiling...
    dotnet run
)

pause
