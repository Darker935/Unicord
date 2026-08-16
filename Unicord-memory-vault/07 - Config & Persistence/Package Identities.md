---
tags:
  - "#config"
  - "#deploy"
aliases:
  - Package Identities
---

# Package Identities

| Package | Manifest | Name | Version (2026-08-13) |
|---------|----------|------|----------------------|
| Canary (this tree) | `Unicord.Universal/Package.appxmanifest` | `24101WamWooWamRD.UnicordCanary` | 2.0.12.0 |
| Store / installed prod | `Unicord.Universal.Package/Package.appxmanifest` | `24101WamWooWamRD.Unicord` | 2.0.51.0 |

Publisher (Canary / local): `CN=0F22111D-EDF0-42F0-B58D-26C4C5C5054B`.

## Rules

- Deploy voice/debug work to **Canary only**.
- Production 2.0.51.0 is Developer-signed with a PFX this machine does **not** have. `Add-AppxPackage` cannot replace it. Do not try.
- Same version + different contents → `0x80073CFB`. Bump Canary version or `Remove-AppxPackage` first.
- Signing for Canary on this machine: cert thumbprint `442FA34278685B740D5D1C0279A757B71D9B1C68` in CurrentUser\My. csproj may list a stale thumbprint; pass the store thumbprint on the MSBuild command line. See [[Building]].

`*.pfx` is gitignored.

---

## Links
- [[Building]]
- [[Deploying Canary]]
