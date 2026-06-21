@echo off
REM Local Build Script for Windows
REM This script builds the project locally using the command-line build method

REM ===== CONFIGURATION =====
REM Update these paths for your system
SET UNITY_PATH="C:\Program Files\Unity\Hub\Editor\2022.3.x\Editor\Unity.exe"
SET PROJECT_PATH=%~dp0..\..\..\..\..
SET BUILD_LOG=%PROJECT_PATH%\build.log

REM Build profile (development, staging, production)
SET BUILD_PROFILE=%1
IF "%BUILD_PROFILE%"=="" SET BUILD_PROFILE=development

REM Build target (Win64, Android, iOS)
SET BUILD_TARGET=%2
IF "%BUILD_TARGET%"=="" SET BUILD_TARGET=Win64

REM ===== BUILD EXECUTION =====
echo ========================================
echo Molca Local Build Script
echo ========================================
echo Unity Path: %UNITY_PATH%
echo Project Path: %PROJECT_PATH%
echo Build Profile: %BUILD_PROFILE%
echo Build Target: %BUILD_TARGET%
echo ========================================
echo.

REM Determine build method based on profile
IF /I "%BUILD_PROFILE%"=="development" (
    SET BUILD_METHOD=Molca.Editor.CommandLineBuild.BuildDevelopment
) ELSE IF /I "%BUILD_PROFILE%"=="staging" (
    SET BUILD_METHOD=Molca.Editor.CommandLineBuild.BuildStaging
) ELSE IF /I "%BUILD_PROFILE%"=="production" (
    SET BUILD_METHOD=Molca.Editor.CommandLineBuild.BuildProduction
) ELSE (
    echo Error: Invalid build profile "%BUILD_PROFILE%"
    echo Valid profiles: development, staging, production
    exit /b 1
)

echo Starting build...
echo Build method: %BUILD_METHOD%
echo.

REM Run Unity build
%UNITY_PATH% ^
    -quit ^
    -batchmode ^
    -nographics ^
    -projectPath "%PROJECT_PATH%" ^
    -buildTarget %BUILD_TARGET% ^
    -executeMethod %BUILD_METHOD% ^
    -logFile "%BUILD_LOG%"

REM Check build result
IF %ERRORLEVEL% EQU 0 (
    echo.
    echo ========================================
    echo BUILD SUCCESSFUL!
    echo ========================================
    echo Build output: %PROJECT_PATH%\Builds
    echo Log file: %BUILD_LOG%
    echo ========================================
    exit /b 0
) ELSE (
    echo.
    echo ========================================
    echo BUILD FAILED!
    echo ========================================
    echo Check log file: %BUILD_LOG%
    echo ========================================
    exit /b 1
)

REM Usage Examples:
REM   local-build-windows.bat                        (builds development for Windows)
REM   local-build-windows.bat production Win64       (builds production for Windows)
REM   local-build-windows.bat staging Android        (builds staging for Android)

