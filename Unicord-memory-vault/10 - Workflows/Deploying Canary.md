---
tags:
  - "#workflow"
aliases:
  - Deploying Canary
---

# Deploying Canary

```powershell
$root = "Unicord.Universal\AppPackages\Unicord.Universal_<ver>_x64_Debug_Test"
$pkg  = Join-Path $root "Unicord.Universal_<ver>_x64_Debug.appx"
$deps = Get-ChildItem (Join-Path $root "Dependencies\x64\*.appx")
Add-AppxPackage -Path $pkg -DependencyPath $deps.FullName -ForceApplicationShutdown
```

**x64 deps only.** Passing every arch’s WinUI package duplicates and fails.

Launch:

```powershell
explorer.exe shell:AppsFolder\24101WamWooWamRD.UnicordCanary_g9xp2jqbzr3wg!App
```

Bump `Unicord.Universal/Package.appxmanifest` version before a content-changing install.

Never target `24101WamWooWamRD.Unicord` (Store 2.0.51.0).

---

## Links
- [[Building]]
- [[Package Identities]]
