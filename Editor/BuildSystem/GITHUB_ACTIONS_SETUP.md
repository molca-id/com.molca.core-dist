# GitHub Actions Integration Guide

This guide explains how to set up and use GitHub Actions with the Molca Build System to trigger remote builds directly from Unity Editor.

## Overview

The GitHub Actions build method allows you to:
- ✅ Trigger remote builds from Unity Editor with one click
- ✅ Offload builds to GitHub's infrastructure (save local resources)
- ✅ Collaborate with team members on builds
- ✅ Keep build history and artifacts in GitHub
- ✅ Integrate with CI/CD pipelines

## Prerequisites

Before you begin, you'll need:

1. **GitHub Repository** - Your Unity project must be in a GitHub repository
2. **GitHub Personal Access Token** - With `workflow` and `repo` permissions
3. **GitHub Actions Workflow** - A workflow file with `workflow_dispatch` trigger

---

## Step 1: Create GitHub Personal Access Token

1. Go to [GitHub Settings > Developer settings > Personal access tokens > Tokens (classic)](https://github.com/settings/tokens)

2. Click **"Generate new token (classic)"**

3. Configure the token:
   - **Name**: `Unity Build System`
   - **Expiration**: Choose based on your security requirements
   - **Scopes**: Check these boxes:
     - ☑️ `repo` (Full control of private repositories)
     - ☑️ `workflow` (Update GitHub Action workflows)

4. Click **"Generate token"** and **copy the token immediately** (you won't be able to see it again!)

---

## Step 2: Set Up Environment Variable (Recommended)

For security, store your token as an environment variable instead of in the project files:

### Windows (PowerShell)
```powershell
# Set for current session
$env:GITHUB_TOKEN = "ghp_your_token_here"

# Set permanently (requires admin)
[System.Environment]::SetEnvironmentVariable('GITHUB_TOKEN', 'ghp_your_token_here', 'User')
```

### macOS/Linux
```bash
# Add to ~/.bashrc or ~/.zshrc
export GITHUB_TOKEN="ghp_your_token_here"

# Then reload your shell
source ~/.bashrc  # or source ~/.zshrc
```

### Alternative: Store in Unity Settings
If you prefer, you can store the token directly in Build Settings (not recommended for team projects):
- Go to Build Settings > GitHub Actions tab
- Uncheck "Use Environment Variable"
- Enter your token in "Personal Access Token"
- ⚠️ **Warning**: This will store the token in the project file!

---

## Step 3: Create GitHub Actions Workflow

Create a file at `.github/workflows/build.yml` in your repository:

```yaml
name: Unity Build

on:
  workflow_dispatch:
    inputs:
      profile:
        description: 'Build Profile'
        required: true
        type: choice
        options:
          - development
          - staging
          - production
        default: 'development'

jobs:
  build:
    name: Build Unity Project
    runs-on: ubuntu-latest
    
    steps:
      - name: Checkout Repository
        uses: actions/checkout@v4
        with:
          fetch-depth: 0
          lfs: true
      
      - name: Cache Unity Library
        uses: actions/cache@v3
        with:
          path: Library
          key: Library-${{ hashFiles('Assets/**', 'Packages/**', 'ProjectSettings/**') }}
          restore-keys: Library-
      
      - name: Build Unity Project
        uses: game-ci/unity-builder@v4
        env:
          UNITY_LICENSE: ${{ secrets.UNITY_LICENSE }}
          UNITY_EMAIL: ${{ secrets.UNITY_EMAIL }}
          UNITY_PASSWORD: ${{ secrets.UNITY_PASSWORD }}
        with:
          targetPlatform: StandaloneWindows64
          buildName: YourGameName
          buildsPath: Builds
      
      - name: Upload Build Artifacts
        uses: actions/upload-artifact@v3
        with:
          name: Build-${{ github.event.inputs.profile }}-${{ github.run_number }}
          path: Builds
          retention-days: 14
```

### Required GitHub Secrets

Set these in your GitHub repository settings (`Settings > Secrets and variables > Actions`):

- `UNITY_LICENSE` - Your Unity license file content
- `UNITY_EMAIL` - Your Unity account email
- `UNITY_PASSWORD` - Your Unity account password

📖 See [Game CI Documentation](https://game.ci/docs/github/activation) for help with Unity license activation.

---

## Step 4: Configure Build Settings in Unity

1. Open your **Build Settings** asset in Unity (create one if you don't have it)

2. Go to the **"GitHub Actions"** tab

3. Fill in the **Repository Settings**:
   - **Repository Owner**: Your GitHub username or organization (e.g., `octocat`)
   - **Repository Name**: Your repository name (e.g., `my-unity-game`)
   - **Workflow File**: Name of your workflow file (e.g., `build.yml`)
   - **Branch**: Branch to run the workflow on (e.g., `main`)

4. Configure **Authentication**:
   - ☑️ **Use Environment Variable**: Checked (recommended)
   - **Environment Variable Name**: `GITHUB_TOKEN` (default)

   Or if not using environment variables:
   - ☐ **Use Environment Variable**: Unchecked
   - **Personal Access Token**: Your GitHub token (⚠️ not recommended)

---

## Step 5: Set Build Method to GitHub Actions

1. In Build Settings, go to **"Build Profiles"** tab

2. Select a profile (Development, Staging, or Production)

3. Set **Build Method** to **"GitHub Actions"**

4. Click the **"Build"** button next to the Build Method dropdown

5. Unity will trigger your GitHub Actions workflow! 🎉

---

## How It Works

```
┌─────────────────────────────────────────┐
│         Unity Editor                    │
│  (You click "Build" button)             │
└──────────────┬──────────────────────────┘
               │
               │ HTTP POST Request
               │ (GitHub API)
               ▼
┌─────────────────────────────────────────┐
│         GitHub Actions                  │
│  • Checkout repository                  │
│  • Setup Unity                          │
│  • Build project                        │
│  • Upload artifacts                     │
└─────────────────────────────────────────┘
```

When you click the Build button:
1. Unity sends an API request to GitHub
2. GitHub triggers the workflow_dispatch event
3. Your workflow runs with the selected profile
4. Build artifacts are saved in GitHub

---

## Troubleshooting

### Error: "No authentication token found"
- **Solution**: Make sure your environment variable is set correctly
- **Windows**: Restart Unity after setting the environment variable
- **macOS/Linux**: Make sure you sourced your shell config file

### Error: "Failed to trigger workflow"
Common causes:
- ❌ **Invalid token**: Generate a new token with correct permissions
- ❌ **Expired token**: Tokens can expire - generate a new one
- ❌ **Wrong repository**: Check owner and repo name spelling
- ❌ **Workflow doesn't exist**: Make sure `.github/workflows/build.yml` exists
- ❌ **No workflow_dispatch trigger**: Your workflow must have `on: workflow_dispatch:`

### Error: "Workflow file 'build.yml' not found"
- **Solution**: Make sure the workflow file exists in the correct branch
- Check that it's in `.github/workflows/` folder
- Verify the branch name in Build Settings matches your repository's default branch

### Build starts but fails
- Check the Unity license secrets are set correctly in GitHub
- Review the workflow logs in GitHub Actions tab
- Make sure your Unity version is compatible with `game-ci/unity-builder`

---

## Best Practices

### Security
- ✅ **Always use environment variables** for tokens (never commit tokens to repository)
- ✅ **Set token expiration** to limit security exposure
- ✅ **Use separate tokens** for different team members
- ✅ **Rotate tokens regularly**

### Workflow Optimization
- ✅ **Use caching** for Unity Library folder (speeds up builds significantly)
- ✅ **Set appropriate retention days** for artifacts (saves storage costs)
- ✅ **Use matrix builds** for multiple platforms
- ✅ **Add notifications** (Discord, Slack, email) for build status

### Team Collaboration
- ✅ **Document workflow requirements** in your project README
- ✅ **Share environment variable setup** instructions with team
- ✅ **Use consistent branch names** across team
- ✅ **Monitor GitHub Actions usage** to stay within limits

---

## Example Workflows

For more advanced workflow examples, see:
- `github-actions-workflow-example.yml` - Complete example with notifications
- [Game CI Documentation](https://game.ci/docs/github/builder) - Official Unity CI/CD docs
- [GitHub Actions Docs](https://docs.github.com/en/actions) - GitHub Actions reference

---

## FAQ

**Q: Can I trigger builds for different platforms?**  
A: Yes! Modify your workflow to accept a platform input, or create separate workflows for each platform.

**Q: How do I download the built artifacts?**  
A: Go to your repository's "Actions" tab, click on the workflow run, and download artifacts from the bottom of the page.

**Q: Does this work with Unity Cloud Build?**  
A: No, this specifically uses GitHub Actions. Unity Cloud Build is a separate service.

**Q: Can I still use CommandLineBuild for local builds?**  
A: Yes! The `CommandLineBuild` class still exists for direct command-line builds. GitHub Actions is just an additional option.

**Q: What are the GitHub Actions usage limits?**  
A: Free accounts get 2,000 minutes/month for private repos, unlimited for public repos. See [GitHub pricing](https://github.com/pricing) for details.

**Q: Can this work with GitLab, Bitbucket, etc?**  
A: Not with this specific implementation, but you can adapt the concept for other platforms' APIs.

---

## Support

For issues or questions:
1. Check the troubleshooting section above
2. Review GitHub Actions workflow logs
3. Check Unity console for error messages
4. Verify all configuration settings are correct

---

## What's Next?

- 🎯 Set up Discord/Slack notifications in your workflow
- 🎯 Add automatic versioning and tagging
- 🎯 Create multi-platform build matrix
- 🎯 Set up automatic deployment to platforms (Steam, itch.io, etc.)
- 🎯 Add automated testing before builds

Happy building! 🚀

