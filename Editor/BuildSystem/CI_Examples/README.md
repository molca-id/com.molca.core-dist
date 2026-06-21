# CI/CD Examples for Molca Build System

This directory contains ready-to-use configuration examples for integrating the Molca Build System with popular CI/CD platforms.

## Quick Start Guide

### 1. Local Testing (Easiest) ⭐

Test the command-line build system locally before setting up CI/CD.

**Windows:**
```batch
cd Assets\_Molca\_Core\Editor\BuildSystem\CI_Examples
local-build-windows.bat development Win64
```

**macOS/Linux:**
```bash
cd Assets/_Molca/_Core/Editor/BuildSystem/CI_Examples
chmod +x local-build-mac.sh
./local-build-mac.sh development StandaloneOSX
```

### 2. Choose Your Platform

| Platform | File | Difficulty | Setup Time |
|----------|------|-----------|------------|
| **GitHub Actions** | `github-actions-example.yml` | ⭐⭐ | 10-15 min |
| **GitLab CI** | `gitlab-ci-example.yml` | ⭐⭐ | 10-15 min |
| **Jenkins** | `jenkins-pipeline-example.groovy` | ⭐⭐⭐ | 20-30 min |
| **Local Scripts** | `local-build-*.bat/sh` | ⭐ | Immediate |

## Platform-Specific Setup

### GitHub Actions

1. Copy `github-actions-example.yml` to `.github/workflows/build.yml` in your repo root
2. Set up repository secrets (Settings > Secrets and variables > Actions):
   - `UNITY_LICENSE` - Your Unity license file content
   - `UNITY_EMAIL` - Your Unity account email
   - `UNITY_PASSWORD` - Your Unity account password
3. Push to trigger the workflow
4. Download builds from Actions > Build > Artifacts

**Pro Tips:**
- Use `game-ci/unity-builder` action for easy Unity integration
- Cache the Library folder to speed up builds
- Set up separate workflows for different branches

### GitLab CI

1. Copy `gitlab-ci-example.yml` to `.gitlab-ci.yml` in your repo root
2. Set up CI/CD variables (Settings > CI/CD > Variables):
   - `UNITY_LICENSE` - Your Unity license file content
   - `UNITY_EMAIL` - Your Unity account email
   - `UNITY_PASSWORD` - Your Unity account password
3. Push to trigger the pipeline
4. Download builds from CI/CD > Jobs > Artifacts

**Pro Tips:**
- Use Unity Docker images (`unityci/editor`) for consistent builds
- Set up different jobs for different platforms
- Use `only:` rules to trigger specific builds per branch

### Jenkins

1. Install Jenkins and required plugins (Git, Pipeline)
2. Create new Pipeline job
3. Copy content from `jenkins-pipeline-example.groovy` to Pipeline script
4. Update `UNITY_PATH` environment variable for your system
5. Click "Build with Parameters" to start

**Pro Tips:**
- Set up Unity license activation on Jenkins machine first
- Use Jenkins credentials manager for sensitive data
- Create separate jobs for different platforms
- Set up webhook triggers from your Git repository

### Local Scripts

**Windows (`local-build-windows.bat`):**
```batch
REM Development build for Windows
local-build-windows.bat development Win64

REM Production build for Android
local-build-windows.bat production Android
```

**macOS/Linux (`local-build-mac.sh`):**
```bash
# Development build for macOS
./local-build-mac.sh development StandaloneOSX

# Production build for iOS
./local-build-mac.sh production iOS
```

## Common Unity License Setup

All CI/CD platforms require a Unity license. Here's how to get it:

### Option 1: Unity Personal/Plus (Recommended for CI)

1. Activate Unity manually on your machine
2. Find the license file:
   - **Windows:** `C:\ProgramData\Unity\Unity_lic.ulf`
   - **macOS:** `/Library/Application Support/Unity/Unity_lic.ulf`
   - **Linux:** `~/.local/share/unity3d/Unity/Unity_lic.ulf`
3. Copy entire file content to `UNITY_LICENSE` secret/variable

### Option 2: Unity Pro (With Serial)

Use Unity's activation tools in CI environments. See Unity documentation for details.

### Option 3: Unity Build Automation License

For commercial projects, consider Unity Build Server license for CI/CD.

## Build Targets

Available build targets for `-buildTarget` parameter:

| Target | Platform | Notes |
|--------|----------|-------|
| `Win64` | Windows 64-bit | Most common desktop target |
| `StandaloneOSX` | macOS | Universal build |
| `Android` | Android | Requires Android Build Support module |
| `iOS` | iOS | Requires iOS Build Support module + macOS |
| `WebGL` | Web | Requires WebGL Build Support module |
| `StandaloneLinux64` | Linux | Requires Linux Build Support module |

Make sure the required build support module is installed in Unity Hub before building.

## Troubleshooting

### Build Fails with "Method not found"

**Cause:** Build method script not compiled or wrong namespace

**Solution:**
1. Open project in Unity Editor once to compile all scripts
2. Verify `CommandLineBuild.cs` exists and compiles without errors
3. Check the method name is exact: `Molca.Editor.CommandLineBuild.BuildProduction`

### Unity License Error

**Cause:** License not activated or invalid

**Solution:**
1. Verify license file content in CI secrets/variables
2. Check license hasn't expired
3. Ensure license type allows headless builds
4. Try activating Unity manually first

### Build Module Not Found

**Cause:** Build target module not installed

**Solution:**
1. Open Unity Hub
2. Go to Installs > [Your Version] > Add Modules
3. Install required build support (e.g., Android Build Support)

### "Permission Denied" on macOS/Linux

**Cause:** Script not executable

**Solution:**
```bash
chmod +x local-build-mac.sh
```

### Build Succeeds but No Output

**Cause:** Build path misconfigured

**Solution:**
1. Check `BuildManager.cs` for build output path
2. Default is `ProjectRoot/Builds/`
3. Check build.log for actual output location

## Advanced Topics

### Custom Build Arguments

Modify `CommandLineBuild.cs` to accept custom arguments:

```csharp
public static void BuildWithProfile()
{
    string[] args = Environment.GetCommandLineArgs();
    string profile = GetArgument(args, "-profile", "development");
    BuildManager.Build(profile);
}
```

Then use:
```
Unity.exe -executeMethod Molca.Editor.CommandLineBuild.BuildWithProfile -profile "staging"
```

### Multiple Platforms in One Build

Create a script that builds for multiple platforms:

```csharp
public static void BuildAllPlatforms()
{
    EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
    BuildProduction();
    
    EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
    BuildProduction();
}
```

### Post-Build Deployment

Add deployment steps after successful build:
- Upload to Steam (SteamPipe)
- Upload to itch.io (Butler)
- Upload to Google Play (fastlane)
- Upload to App Store (fastlane)
- Deploy to internal server (rsync/scp)

See platform-specific examples for deployment job templates.

## Resources

- [Unity Command Line Arguments](https://docs.unity3d.com/Manual/CommandLineArguments.html)
- [Unity Cloud Build](https://unity.com/products/cloud-build)
- [GameCI Documentation](https://game.ci/)
- [BUILD_METHODS.md](../BUILD_METHODS.md) - Detailed build methods guide

## Support

If you encounter issues:
1. Check build.log for detailed error messages
2. Review BUILD_METHODS.md for setup instructions
3. Verify Unity version matches CI configuration
4. Ensure all build modules are installed

