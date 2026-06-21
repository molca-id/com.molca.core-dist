# GitHub Actions Integration - Quick Start

## 🎯 What's New?

The Molca Build System now supports **triggering GitHub Actions workflows** directly from Unity Editor!

### Before ❌
```
Unity Editor → Build locally → Share build files manually
```

### After ✅
```
Unity Editor → Click Build → GitHub Actions builds → Download from GitHub
```

---

## 📚 Documentation

| Document | Description |
|----------|-------------|
| **[GITHUB_ACTIONS_SETUP.md](./GITHUB_ACTIONS_SETUP.md)** | Complete setup guide (start here!) |
| **[GITHUB_ACTIONS_MIGRATION.md](./GITHUB_ACTIONS_MIGRATION.md)** | Migration guide from CommandLine method |
| **[github-actions-workflow-example.yml](./github-actions-workflow-example.yml)** | Example workflow file |

---

## ⚡ Quick Setup (5 minutes)

### 1. Get GitHub Token
1. Go to [GitHub Settings > Personal Access Tokens](https://github.com/settings/tokens)
2. Generate new token with `workflow` and `repo` permissions
3. Set as environment variable:
   ```powershell
   # Windows
   $env:GITHUB_TOKEN = "your_token_here"
   ```
   ```bash
   # macOS/Linux
   export GITHUB_TOKEN="your_token_here"
   ```

### 2. Create Workflow File
Create `.github/workflows/build.yml` in your repository with:
```yaml
name: Unity Build

on:
  workflow_dispatch:
    inputs:
      profile:
        required: true
        type: choice
        options: [development, staging, production]

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: game-ci/unity-builder@v4
        # ... see full example in github-actions-workflow-example.yml
```

### 3. Configure Unity
1. Open **Build Settings** in Unity
2. Go to **"GitHub Actions"** tab
3. Fill in:
   - Repository Owner: `your-username`
   - Repository Name: `your-repo`
   - Workflow File: `build.yml`
   - Branch: `main`

### 4. Build!
1. Go to **"Build Profiles"** tab
2. Select a profile (Development/Staging/Production)
3. Set **Build Method** to **"GitHub Actions"**
4. Click the **Build** button 🚀

---

## ✨ Features

- ✅ **One-Click Remote Builds** - Trigger builds from Unity Editor
- ✅ **Secure Authentication** - Environment variable support (no tokens in repo)
- ✅ **Team Collaboration** - Share builds via GitHub Artifacts
- ✅ **Resource Efficient** - Build on GitHub's infrastructure
- ✅ **Build History** - Track all builds in GitHub Actions
- ✅ **Easy Configuration** - Dedicated UI tab with validation

---

## 🔧 Configuration

### In Unity (Build Settings > GitHub Actions)
- Repository owner & name
- Workflow file name
- Authentication (token or environment variable)
- Branch to build

### In GitHub (.github/workflows/build.yml)
- Build platform (Windows/Mac/Linux/Android/iOS)
- Unity version
- Build settings
- Artifact retention
- Notifications (optional)

---

## 🆚 Build Method Comparison

| Method | Use Case | Builds On | Best For |
|--------|----------|-----------|----------|
| **Editor** | Local builds | Your machine | Daily development, quick testing |
| **CommandLine** | CI/CD marker | Your machine | Traditional CI/CD (Jenkins, GitLab, etc.) |
| **GitHubActions** | Remote builds | GitHub servers | Team builds, remote CI/CD, offloading work |

**Note**: All three methods are available. Choose based on your workflow needs!

---

## 🐛 Troubleshooting

### "No authentication token found"
→ Set `GITHUB_TOKEN` environment variable (restart Unity after setting)

### "Failed to trigger workflow"
→ Check token permissions (needs `workflow` scope)  
→ Verify repository owner/name spelling  
→ Ensure workflow file exists in the specified branch

### "Workflow not found"
→ Make sure `.github/workflows/build.yml` exists  
→ Check that workflow has `workflow_dispatch:` trigger  
→ Verify branch name matches

See **[GITHUB_ACTIONS_SETUP.md](./GITHUB_ACTIONS_SETUP.md)** for detailed troubleshooting.

---

## 📖 Learn More

- **[Setup Guide](./GITHUB_ACTIONS_SETUP.md)** - Detailed setup instructions
- **[Migration Guide](./GITHUB_ACTIONS_MIGRATION.md)** - Upgrading from CommandLine
- **[Game CI Docs](https://game.ci/docs/github/builder)** - Unity CI/CD documentation
- **[GitHub Actions Docs](https://docs.github.com/en/actions)** - GitHub Actions reference

---

## 💡 Pro Tips

1. **Use environment variables** for tokens (never commit them!)
2. **Enable caching** in workflow for faster builds
3. **Set up notifications** (Discord/Slack) for build status
4. **Monitor GitHub Actions minutes** usage for your account
5. **Create separate workflows** for different platforms

---

## 🤝 Need Help?

1. Read the [Setup Guide](./GITHUB_ACTIONS_SETUP.md)
2. Check the [Migration Guide](./GITHUB_ACTIONS_MIGRATION.md) 
3. Review Unity console for detailed error messages
4. Verify configuration in Build Settings

---

## 🚀 What's Next?

- Set up multi-platform builds (Windows, macOS, Linux, Android, iOS)
- Add automated testing before builds
- Configure Discord/Slack notifications
- Set up automatic deployment (Steam, itch.io, etc.)
- Create release tags automatically

Happy building! 🎮

