# Molca Build System - Quick Reference Card

## 🎯 Build Methods at a Glance

**Build Methods control which pipeline is used:**

| Method | What It Does | From Inspector | From CI/CD |
|--------|-------------|----------------|------------|
| **Editor** | Standard Unity build | BuildPipeline.BuildPlayer() | N/A (use CommandLine) |
| **CommandLine** | CI/CD profile marker | Same as Editor | Builds then exits |

---

## 🎯 How to Trigger Builds

### Inspector Button (Recommended! 🎨)
1. Open Build Settings asset
2. Go to "Build Profiles" tab  
3. Select profile tab (Development/Staging/Production)
4. Set "Build Method" to desired method
5. Click the colored **"Build"** button next to Build Method

### Keyboard Shortcuts (Quick! ⚡)

| Shortcut | Action |
|----------|--------|
| `Ctrl+Alt+D` | Build Development |
| `Ctrl+Alt+S` | Build Staging |
| `Ctrl+Alt+P` | Build Production |

Access via: **Molca > Build > [Profile]**

### From Code
```csharp
BuildManager.Build("development");  // or "staging" / "production"
```

---

## 💻 Command Line Usage

### Windows
```batch
"C:\Program Files\Unity\Hub\Editor\2022.3.x\Editor\Unity.exe" ^
  -quit -batchmode -nographics ^
  -projectPath "C:\Your\Project\Path" ^
  -buildTarget Win64 ^
  -executeMethod Molca.Editor.CommandLineBuild.BuildProduction ^
  -logFile build.log
```

### macOS/Linux
```bash
/Applications/Unity/.../Unity \
  -quit -batchmode -nographics \
  -projectPath "/your/project/path" \
  -buildTarget StandaloneOSX \
  -executeMethod Molca.Editor.CommandLineBuild.BuildProduction \
  -logFile build.log
```

---

## 📦 Available Build Methods

### From Code
```csharp
BuildManager.Build("development");  // or "staging" or "production"
```

### From Command Line
- `Molca.Editor.CommandLineBuild.BuildDevelopment`
- `Molca.Editor.CommandLineBuild.BuildStaging`
- `Molca.Editor.CommandLineBuild.BuildProduction`
- `Molca.Editor.CommandLineBuild.BuildWithProfile` (with `-profile "name"`)

---

## 🎮 Build Targets

| Target | Platform | Unity Module Required |
|--------|----------|----------------------|
| `Win64` | Windows 64-bit | Windows Build Support |
| `StandaloneOSX` | macOS | macOS Build Support |
| `Android` | Android | Android Build Support |
| `iOS` | iOS | iOS Build Support (macOS only) |
| `WebGL` | Web Browser | WebGL Build Support |
| `StandaloneLinux64` | Linux | Linux Build Support |

---

## 🏗️ Build Profiles

Each profile has configurable options:

### Development
- Fast iteration
- Debug symbols enabled
- Development build flags
- Auto-increments dev version

### Staging  
- Testing/QA builds
- Closer to production settings
- Auto-increments staging version

### Production
- Release builds
- Optimized settings
- Only increments build number

---

## 🔧 Build Options

Configure in Build Settings asset:

| Option | Description |
|--------|-------------|
| **Development Build** | Enables profiler, debug symbols |
| **Allow Debugging** | Enables script debugging |
| **IL2CPP** | Use IL2CPP instead of Mono (better performance) |
| **Compress** | Enable LZ4HC compression |

---

## 🚀 Quick Start - Local Testing

### 1. Using Inspector Button (Easiest! 🎯)
1. Select Build Settings asset in Project window
2. Click "Build Profiles" tab
3. Select "Development" tab
4. Ensure "Build Method" is set to "Editor"
5. Click the blue **"Build"** button (inline with Build Method)
6. Check `Builds/` folder for output

**That's it!** Inline button keeps the UI clean and settings visible.

### 2. Using Menu Shortcuts (Fast! ⚡)
1. Press `Ctrl+Alt+D` anywhere in Unity
2. Wait for build to complete
3. Check `Builds/` folder

### 3. Using Local Scripts
```batch
# Windows
cd Assets\_Molca\_Core\Editor\BuildSystem\CI_Examples
local-build-windows.bat development Win64

# macOS/Linux
cd Assets/_Molca/_Core/Editor/BuildSystem/CI_Examples
chmod +x local-build-mac.sh
./local-build-mac.sh development StandaloneOSX
```

---

## 🤖 CI/CD Quick Setup

### GitHub Actions (5 minutes)
1. Copy `CI_Examples/github-actions-example.yml` → `.github/workflows/build.yml`
2. Add secrets: `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`
3. Push to repository
4. ✅ Automatic builds on push!

### GitLab CI (5 minutes)
1. Copy `CI_Examples/gitlab-ci-example.yml` → `.gitlab-ci.yml`
2. Add CI/CD variables: `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`
3. Push to repository
4. ✅ Automatic builds on push!

### Jenkins (15 minutes)
1. Install Jenkins + Pipeline plugin
2. Create new Pipeline job
3. Copy script from `CI_Examples/jenkins-pipeline-example.groovy`
4. Update `UNITY_PATH` for your system
5. ✅ Build with parameters!

---

## 📍 File Locations

| What | Where |
|------|-------|
| Build Settings | Project Settings > Molca Settings |
| Build Output | `ProjectRoot/Builds/` |
| Build Logs | `ProjectRoot/build.log` |
| Version Settings | Project Settings > Molca Settings |
| Documentation | `Assets/_Molca/_Core/Editor/BuildSystem/` |

---

## 🔔 Webhook Notifications

Discord/Slack notifications on build events:

1. Open Build Settings asset
2. Go to "Notifications" tab
3. Enable webhook
4. Paste webhook URL
5. ✅ Get notified on builds!

Supports:
- Build started/completed notifications
- Light baking notifications
- User/role mentions
- Thread support

---

## 📚 Documentation Files

| File | Purpose |
|------|---------|
| `BUILD_METHODS.md` | Detailed guide for each build method |
| `CI_Examples/README.md` | CI/CD setup instructions |
| `QUICK_REFERENCE.md` | This file - quick lookup |

---

## 🐛 Common Issues

### Build Fails
- ✅ Check `build.log` for errors
- ✅ Verify Unity version matches
- ✅ Ensure build target module installed

### License Error (CI/CD)
- ✅ Verify `UNITY_LICENSE` secret is set
- ✅ Check license hasn't expired
- ✅ Ensure license allows headless builds

### Method Not Found
- ✅ Open project in Unity once to compile scripts
- ✅ Verify `CommandLineBuild.cs` exists
- ✅ Check method name is exact

### No Build Output
- ✅ Check `Builds/` folder in project root
- ✅ Review `build.log` for output location
- ✅ Verify build completed successfully

---

## 💡 Pro Tips

1. **Use inline inspector button** - Clean UI, settings visible while building
2. **Use menu shortcuts for quick iterations** - `Ctrl+Alt+D` is fast when you know the settings
3. **Build Method now controls execution** - Different methods use different pipelines
4. **Editor = CommandLine from inspector** - Both use BuildPipeline, choose based on intent
5. **Set CI/CD profiles to CommandLine method** - Makes their purpose clear
6. **Cache Unity Library folder** - Speeds up CI builds significantly
7. **Check build.log** - First place to look for errors

---

## 🎓 Learning Path

1. ⭐ **Start Here:** Use inspector button (visual, easy to understand)
2. ⭐ **Next:** Learn menu shortcuts (press `Ctrl+Alt+D` for speed)
3. ⭐⭐ **Then:** Try local scripts or set up CI/CD
4. ⭐⭐⭐ **Advanced:** Configure advanced asset management

---

## 📞 Need More Help?

- 📖 Read [BUILD_METHODS.md](./BUILD_METHODS.md) for detailed guides
- 🔧 Check [CI_Examples/README.md](./CI_Examples/README.md) for CI/CD setup
- 🔍 Search Unity documentation for Unity-specific issues
- 💬 Ask your team for project-specific configuration

---

**Last Updated:** 2025
**Version:** 1.0
**Maintained by:** Molca Team

