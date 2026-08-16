---
tags:
  - "#workflow"
aliases:
  - Build
---

# Building

## Prerequisites (README)

- Windows 11 22000+
- Windows 11 SDK 26100
- VS 2022+ with UWP / WinUI + UWP tools
- Submodules: `git submodule update --init --recursive`

## Canary (what we deploy)

MSBuild (VS 18 path on this machine):

```powershell
& "${env:ProgramFiles}\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
  Unicord.Universal\Unicord.Universal.csproj `
  /p:Configuration=Debug /p:Platform=x64 `
  /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly `
  /p:AppxPackageSigningEnabled=true `
  /p:PackageCertificateThumbprint=442FA34278685B740D5D1C0279A757B71D9B1C68 `
  /p:PackageCertificateKeyFile=
```

Output: `Unicord.Universal\AppPackages\Unicord.Universal_<ver>_x64_Debug_Test\`

Compile can succeed while packaging fails if the csproj thumbprint does not match the store cert. Prefer the thumbprint above.

The committed csproj names `2B9388FE3BA91067A7367141578ADD1436AEB76C` and a password-protected `Unicord.Universal_TemporaryKey.pfx`. Both are WamWooWam's and exist on no other machine, and `.gitignore` excludes `*.pfx`, so a fresh clone cannot sign anything until it has a certificate of its own. `tools/New-SigningCertificate.ps1` creates one, trusts it locally, and uploads it as the secrets CI signs with. It prints the thumbprint to use above.

## DAVE native

Only when `Voice/native/unicord_dave` changes:

```powershell
cd Unicord.Universal\Voice\native\unicord_dave
cargo build --release
Copy-Item target\release\unicord_dave.dll ..\unicord_dave.dll -Force
```

csproj already Content-links that DLL to AppX root.

## F5

Open `Unicord.sln`, Debug x64, F5 → Start menu **Unicord Canary**.

---

## Links
- [[Deploying Canary]]
- [[Package Identities]]
