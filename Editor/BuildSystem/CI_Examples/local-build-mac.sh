#!/bin/bash
# Local Build Script for macOS/Linux
# This script builds the project locally using the command-line build method

# ===== CONFIGURATION =====
# Update these paths for your system
UNITY_PATH="/Applications/Unity/Hub/Editor/2022.3.x/Unity.app/Contents/MacOS/Unity"
PROJECT_PATH="$(cd "$(dirname "$0")/../../../../../" && pwd)"
BUILD_LOG="$PROJECT_PATH/build.log"

# Build profile (development, staging, production)
BUILD_PROFILE=${1:-development}

# Build target (StandaloneOSX, Win64, Android, iOS)
BUILD_TARGET=${2:-StandaloneOSX}

# ===== BUILD EXECUTION =====
echo "========================================"
echo "Molca Local Build Script"
echo "========================================"
echo "Unity Path: $UNITY_PATH"
echo "Project Path: $PROJECT_PATH"
echo "Build Profile: $BUILD_PROFILE"
echo "Build Target: $BUILD_TARGET"
echo "========================================"
echo ""

# Determine build method based on profile
case "$BUILD_PROFILE" in
    development)
        BUILD_METHOD="Molca.Editor.CommandLineBuild.BuildDevelopment"
        ;;
    staging)
        BUILD_METHOD="Molca.Editor.CommandLineBuild.BuildStaging"
        ;;
    production)
        BUILD_METHOD="Molca.Editor.CommandLineBuild.BuildProduction"
        ;;
    *)
        echo "Error: Invalid build profile '$BUILD_PROFILE'"
        echo "Valid profiles: development, staging, production"
        exit 1
        ;;
esac

echo "Starting build..."
echo "Build method: $BUILD_METHOD"
echo ""

# Run Unity build
"$UNITY_PATH" \
    -quit \
    -batchmode \
    -nographics \
    -projectPath "$PROJECT_PATH" \
    -buildTarget "$BUILD_TARGET" \
    -executeMethod "$BUILD_METHOD" \
    -logFile "$BUILD_LOG"

# Check build result
if [ $? -eq 0 ]; then
    echo ""
    echo "========================================"
    echo "BUILD SUCCESSFUL!"
    echo "========================================"
    echo "Build output: $PROJECT_PATH/Builds"
    echo "Log file: $BUILD_LOG"
    echo "========================================"
    exit 0
else
    echo ""
    echo "========================================"
    echo "BUILD FAILED!"
    echo "========================================"
    echo "Check log file: $BUILD_LOG"
    echo "========================================"
    exit 1
fi

# Usage Examples:
#   ./local-build-mac.sh                           (builds development for macOS)
#   ./local-build-mac.sh production StandaloneOSX  (builds production for macOS)
#   ./local-build-mac.sh staging iOS               (builds staging for iOS)

# Note: You may need to make this script executable first:
#   chmod +x local-build-mac.sh

