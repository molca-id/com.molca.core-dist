# Molca Build System - Build Methods Guide

This document explains the different build methods available in the Molca build system and how to use them.

## Important Concept: Build Methods

**Build Methods** control which build pipeline is used when you click the "Build" button in the inspector:

- **Editor** - Standard Unity BuildPipeline.BuildPlayer()
- **CommandLine** - Same as Editor when triggered from inspector (useful for marking CI/CD profiles)

**Note:** Editor and CommandLine methods are functionally identical when triggered from the Unity Editor. The distinction is useful for indicating that a profile is intended for CI/CD use.

## Build Methods Overview

| Method | Intended Use | Typical Triggers | Setup Time |
|--------|-------------|------------------|------------|
| **Editor** | Manual builds, testing | Inspector button, menu shortcuts, code | Immediate |
| **CommandLine** | CI/CD automation | Command line with -executeMethod | 5-10 minutes |

---

## 1. Editor Method ⭐

**Use Case:** Standard manual builds from Unity Editor using Unity's built-in BuildPipeline.

**Setup:**
- No setup required!
- Default method, works out of the box

**How It Works:**
Uses `BuildPipeline.BuildPlayer()` with the profile's configured options.

**How to Trigger:**

### Option A: Inspector Button (Recommended! 🎯)
1. Select your Build Settings asset in the Project window
2. Switch to the "Build Profiles" tab
3. Select the profile you want (Development/Staging/Production)
4. Set "Build Method" to "Editor"
5. Click the colored **"Build"** button next to Build Method field

### Option B: Menu Shortcuts (Quick Access! ⚡)
- **Development**: `Molca > Build > Development Build` or press `Ctrl+Alt+D`
- **Staging**: `Molca > Build > Staging Build` or press `Ctrl+Alt+S`
- **Production**: `Molca > Build > Production Build` or press `Ctrl+Alt+P`

### Option C: From Code
```csharp
BuildManager.Build("development");  // or "staging" or "production"
```

**Benefits:**
- Inline build button right next to method selector
- Fast access via keyboard shortcuts
- Standard, reliable Unity build pipeline
- Configure and build in one place
- Great for quick iteration during development

---

## 2. CommandLine Method ⭐⭐

**Use Case:** Profiles intended for CI/CD automation. When triggered from inspector, behaves the same as Editor method.

**Setup Time:** 5-10 minutes (for CI/CD integration)

**How It Works:**
- From Inspector: Same as Editor method (calls `BuildManager.Build()`)
- From CI/CD: Calls `CommandLineBuild.Build[Profile]()` which builds then exits Unity
- Useful for marking profiles that are designed for automated builds

### Windows Example:

```batch
"C:\Program Files\Unity\Hub\Editor\2022.3.x\Editor\Unity.exe" ^
  -quit ^
  -batchmode ^
  -nographics ^
  -projectPath "C:\Path\To\Your\Project" ^
  -buildTarget Win64 ^
  -executeMethod Molca.Editor.CommandLineBuild.BuildProduction ^
  -logFile build.log
```

### macOS/Linux Example:

```bash
/Applications/Unity/Hub/Editor/2022.3.x/Unity.app/Contents/MacOS/Unity \
  -quit \
  -batchmode \
  -nographics \
  -projectPath "/path/to/your/project" \
  -buildTarget StandaloneOSX \
  -executeMethod Molca.Editor.CommandLineBuild.BuildProduction \
  -logFile build.log
```

### Available Methods:
- `Molca.Editor.CommandLineBuild.BuildDevelopment`
- `Molca.Editor.CommandLineBuild.BuildStaging`
- `Molca.Editor.CommandLineBuild.BuildProduction`
- `Molca.Editor.CommandLineBuild.BuildWithProfile` (with `-profile "name"` argument)

### Ready-to-Use CI/CD Examples

Complete configuration files are available in the `CI_Examples/` folder:
- ✅ **GitHub Actions** - `.github/workflows/build.yml`
- ✅ **GitLab CI** - `.gitlab-ci.yml`  
- ✅ **Jenkins** - Pipeline script
- ✅ **Local Scripts** - Windows `.bat` and macOS/Linux `.sh`

See [CI_Examples/README.md](./CI_Examples/README.md) for detailed setup instructions and usage examples.

**Benefits:**
- Automated builds on code push
- Consistent build environment
- Integrates with webhooks for Discord/Slack notifications
- Build logs saved automatically

---


## Comparison & Recommendations

### For Solo Development:
✅ **Editor Method with Menu Shortcuts** - Fast, easy access with keyboard shortcuts (`Ctrl+Alt+D`)

### For Small Team:
✅ **Editor Method** - Perfect for manual builds with convenient hotkeys

### For CI/CD Pipeline:
✅ **CommandLine Method** - Automated, repeatable builds

---

## Current Build Features

All build methods include:
- ✅ Automatic version incrementing
- ✅ Profile-based configuration (Development/Staging/Production)
- ✅ IL2CPP/Mono2x support
- ✅ Compression options
- ✅ Development/debugging flags
- ✅ Discord webhook notifications
- ✅ Multi-platform support (Windows, Android, iOS)
- ✅ Build reports and logging

---

## Troubleshooting

### Command Line Build Fails
- Check Unity installation path
- Verify project path is correct
- Ensure build target is installed (File > Build Settings > Install Module)
- Check build.log for detailed errors

### Webhook Notifications Not Sending
- Verify webhook URL in Build Settings
- Check firewall/network settings
- Enable webhook in Build Settings inspector

### Version Not Incrementing
- Check Version Settings are configured in Project Settings
- Ensure VersionSettings asset exists

---

## Need Help?

For more information, see:
- BuildManager.cs - Main build logic
- CommandLineBuild.cs - CLI build methods
- BuildMenuItems.cs - Menu item definitions
- Build Settings asset - Configuration

