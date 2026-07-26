---
title: Add-ons
category: Tooling
order: 920
---

# Add-ons

Add-ons provide optional Molca capabilities. Open **Molca → Hub → Settings → Add-ons** to see the add-ons
available to the connected backend project and compatible with this version of Molca Core.

## Install or update

1. Sign in through **Molca → License → Developer Sign-In** and connect the repository to a backend project.
2. Open **Settings → Add-ons** and select **Refresh**.
3. Review the version, publisher, dependency tree, external packages, and whether a player rebuild is required.
4. Choose **Install** or **Update**, then approve the confirmation.
5. Allow Unity to refresh and finish compiling. Molca resumes the approved transaction after the domain reload.

Owners and managers can approve an add-on for the connected project. Approval includes its complete dependency
closure. Contributors can install approved add-ons but cannot change project policy.

Editor-only add-ons do not affect player builds. If an add-on contains runtime functionality, rebuild the
player after installing or updating it.

## Remove

Choose **Remove** beside an installed add-on and approve the confirmation. Removal is blocked if another
installed add-on still depends on it. Molca keeps a recoverable copy under `Library/Molca/Addons/Recovery/`.

## Install an offline bundle

If your administrator provides an offline bundle, choose **Import signed bundle…** and select its
`.molca-manifest` file and matching `.tgz` file. The import is rejected when the files are damaged,
incompatible, not issued by a trusted Molca publisher, or declares dependencies. Dependency-bearing offline
installation requires a signed closure bundle, which is not yet available.

## Troubleshooting

- **Nothing appears:** sign in, connect this repository to a project, select **Refresh**, and ask a project
  owner or manager to confirm that the add-on closure is approved.
- **No compatible version:** update Molca Core or ask for a version compatible with your current project.
- **Verification failed:** do not bypass the warning or install the archive manually. Download a fresh copy
  or contact your administrator.
- **Unity is still compiling:** wait for compilation and the domain reload to finish before reopening the Hub.
- **A runtime add-on changed:** rebuild the player before testing or distributing it.

## See also

- [Developer Sign-In](LICENSING.md)
- [The Molca Hub](HUB.md)
