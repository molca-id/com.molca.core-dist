---
title: Developer Sign-In
category: Tooling
order: 915
---

# Developer Sign-In

Some Molca distributions require an authorized Google account before you can create a build or download
licensed add-ons. Authorization is stored for this machine and renewed by signing in again when it expires.

## Sign in

1. Open **Molca → License → Developer Sign-In**.
2. Choose **Sign in with Google**.
3. Complete the browser sign-in with your authorized work account.
4. Return to Unity and confirm that the window shows **Licensed** and an expiry date.

You can also see the current license state in the Molca Hub. To remove this machine's saved authorization,
use **Sign out (clear entitlement)** in the sign-in window.

## Build and add-on access

When developer sign-in is required, Unity blocks builds until the current machine is authorized. The Add-ons
workspace uses the same account authorization to show the packages available to you.

## Troubleshooting

- **Not signed in:** open the sign-in window and complete Google sign-in.
- **Expired or invalid:** sign in again to replace the saved authorization.
- **Issued for a different machine:** sign in on the machine that is running Unity.
- **Account not allowed:** use the authorized work account or contact your Molca administrator.
- **Browser flow canceled or failed:** retry, then check the Unity Console for the reported reason.

## See also

- [Add-ons](ADDONS.md)
- [Build System & Versioning](BUILD_SYSTEM.md)
