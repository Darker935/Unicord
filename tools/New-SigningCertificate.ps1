<#
.SYNOPSIS
    Creates the self-signed certificate that signs public Unicord builds, and uploads
    it to GitHub Actions as the secrets the Public Build workflow expects.

.DESCRIPTION
    Sideloaded appx packages must be signed by a certificate whose subject matches the
    manifest Publisher string exactly, and that certificate has to be trusted on every
    machine that installs the package. This script produces one, stores its private half
    as a GitHub secret so CI can sign with it, and optionally trusts it locally so builds
    from this machine install too.

    Every prompt has a default. Press Enter to accept it.

    The password you type is never displayed, never written to disk in the clear, and
    never leaves this machine except as the encrypted .pfx inside a GitHub secret.

.NOTES
    Run from the repository root:  .\tools\New-SigningCertificate.ps1
    Requires the GitHub CLI, authenticated:  gh auth status
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Read-WithDefault {
    param([string]$Prompt, [string]$Default)
    $entered = Read-Host "$Prompt [$Default]"
    if ([string]::IsNullOrWhiteSpace($entered)) { return $Default }
    return $entered.Trim()
}

function Read-YesNo {
    param([string]$Prompt, [bool]$DefaultYes = $true)
    $hint = if ($DefaultYes) { 'Y/n' } else { 'y/N' }
    $entered = (Read-Host "$Prompt [$hint]").Trim()
    if ([string]::IsNullOrWhiteSpace($entered)) { return $DefaultYes }
    return $entered -match '^(y|yes)$'
}

Write-Host ''
Write-Host 'Unicord signing certificate setup' -ForegroundColor Cyan
Write-Host '---------------------------------'
Write-Host ''

# --- prerequisites ---------------------------------------------------------

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "The GitHub CLI (gh) is not on PATH. Install it from https://cli.github.com and run this again."
}

$null = gh auth status 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "The GitHub CLI is not authenticated. Run 'gh auth login' and try again."
}

# --- questions -------------------------------------------------------------

# Must match Publisher in the manifest the workflow builds. The workflow rewrites the
# manifest to CN=Darker935, and asserts the two agree before signing, because a mismatch
# produces a package Windows refuses to install.
$subject   = Read-WithDefault 'Certificate subject (must match the manifest Publisher)' 'CN=Darker935'
$repo      = Read-WithDefault 'GitHub repository to receive the secrets' 'Darker935/Unicord'
$years     = [int](Read-WithDefault 'Validity in years' '3')
$pfxPath   = Read-WithDefault 'Where to save the .pfx' (Join-Path $env:USERPROFILE 'unicord-signing.pfx')
$trustHere = Read-YesNo 'Also trust this certificate on this machine, so local builds install?' $true

Write-Host ''
Write-Host 'Choose a password for the .pfx. You will not see it as you type.' -ForegroundColor Yellow
Write-Host 'It is stored as a GitHub secret so CI can open the file. Keep a copy in your password manager.'
$password = Read-Host 'Password' -AsSecureString
$confirm  = Read-Host 'Confirm password' -AsSecureString

$pwPlain      = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($password))
$confirmPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirm))
if ($pwPlain -ne $confirmPlain) { throw 'The passwords do not match. Nothing was created.' }
if ([string]::IsNullOrWhiteSpace($pwPlain)) { throw 'An empty password is not accepted. Nothing was created.' }

# --- certificate -----------------------------------------------------------

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) }

if ($existing) {
    Write-Host ''
    Write-Host "A usable certificate for $subject already exists:" -ForegroundColor Yellow
    $existing | ForEach-Object { Write-Host "  $($_.Thumbprint)  expires $($_.NotAfter.ToString('yyyy-MM-dd'))" }
    if (Read-YesNo 'Reuse the newest one instead of creating another?' $true) {
        $cert = $existing | Sort-Object NotAfter -Descending | Select-Object -First 1
    }
}

if (-not $cert) {
    Write-Host ''
    Write-Host "Creating a code signing certificate for $subject ..."
    # 2.5.29.37={text}1.3.6.1.5.5.7.3.3 is the Code Signing EKU; without it signtool
    # refuses the certificate. The empty 2.5.29.19 marks it as an end-entity, not a CA.
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -KeyUsage DigitalSignature `
        -FriendlyName 'Unicord sideload signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears($years) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

Write-Host "  thumbprint : $($cert.Thumbprint)"
Write-Host "  expires    : $($cert.NotAfter.ToString('yyyy-MM-dd'))"

# --- export and upload -----------------------------------------------------

Write-Host ''
Write-Host "Exporting to $pfxPath ..."
$null = Export-PfxCertificate -Cert "Cert:\CurrentUser\My\$($cert.Thumbprint)" -FilePath $pfxPath -Password $password

Write-Host "Uploading secrets to $repo ..."
# Piped through stdin rather than passed as --body, so neither the key nor the password
# appears in this machine's process list while gh runs.
$base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfxPath))
$base64 | gh secret set SIGNING_PFX_BASE64 --repo $repo
if ($LASTEXITCODE -ne 0) { throw 'Failed to set SIGNING_PFX_BASE64.' }
$pwPlain | gh secret set SIGNING_PFX_PASSWORD --repo $repo
if ($LASTEXITCODE -ne 0) { throw 'Failed to set SIGNING_PFX_PASSWORD.' }

$base64 = $null
[GC]::Collect()

# --- local trust -----------------------------------------------------------

if ($trustHere) {
    Write-Host ''
    Write-Host 'Trusting the certificate for local installs ...'
    # TrustedPeople is what Add-AppxPackage checks for a sideloaded package. Needs
    # elevation for the machine store; falls back to the user store, which covers
    # installs performed by this user.
    try {
        Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople -Password $password -ErrorAction Stop | Out-Null
        Write-Host '  installed into LocalMachine\TrustedPeople'
    }
    catch {
        Write-Warning "  could not write to LocalMachine\TrustedPeople ($($_.Exception.Message))"
        Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation Cert:\CurrentUser\TrustedPeople -Password $password | Out-Null
        Write-Host '  installed into CurrentUser\TrustedPeople instead; re-run as Administrator to trust it machine-wide'
    }
}

$pwPlain = $null
$confirmPlain = $null
[GC]::Collect()

# --- what to do next -------------------------------------------------------

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host ''
Write-Host 'The next Public Build run will produce signed, installable packages.'
Write-Host ''
Write-Host 'For local builds, this is the thumbprint to pass, as described in'
Write-Host 'Unicord-memory-vault/10 - Workflows/Building.md:'
Write-Host ''
Write-Host "    /p:PackageCertificateThumbprint=$($cert.Thumbprint) /p:PackageCertificateKeyFile=" -ForegroundColor Cyan
Write-Host ''
Write-Host "Keep $pfxPath and its password somewhere safe. Losing them means"
Write-Host 'generating a new certificate, which every user then has to trust again.'
Write-Host ''
Write-Host "This certificate expires $($cert.NotAfter.ToString('yyyy-MM-dd'))." -ForegroundColor Yellow
