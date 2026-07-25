---
title: Add-ons
category: Tooling
order: 920
---

# Add-ons

Add-ons provide optional Molca capabilities. Open **Molca → Hub → Settings → Add-ons** to see the add-ons
available to your account and compatible with this version of Molca Core.

## Install or update

1. Sign in through **Molca → License → Developer Sign-In** if the Hub asks you to.
2. Open **Settings → Add-ons** and select **Refresh**.
3. Review the version, publisher, and whether a player rebuild is required.
4. Choose **Install** or **Update**, then approve the confirmation.
5. Allow Unity to refresh and finish compiling before using the new capability.

Editor-only add-ons do not affect player builds. If an add-on contains runtime functionality, rebuild the
player after installing or updating it.

## Remove

Choose **Remove** beside an installed add-on and approve the confirmation. Molca keeps a recoverable copy
under `Library/Molca/Addons/Recovery/` in case project recovery is needed.

## Install an offline bundle

If your administrator provides an offline bundle, choose **Import signed bundle…** and select its
`.molca-manifest` file and matching `.tgz` file. The import is rejected when the files are damaged,
incompatible, or not issued by a trusted Molca publisher.

## Troubleshooting

- **Nothing appears:** sign in, select **Refresh**, and confirm with your administrator that your account
  has access to the expected add-on.
- **No compatible version:** update Molca Core or ask for a version compatible with your current project.
- **Verification failed:** do not bypass the warning or install the archive manually. Download a fresh copy
  or contact your administrator.
- **Unity is still compiling:** wait for compilation and the domain reload to finish before reopening the Hub.
- **A runtime add-on changed:** rebuild the player before testing or distributing it.

## See also

- [Developer Sign-In](LICENSING.md)
- [The Molca Hub](HUB.md)
