<#
.SYNOPSIS
    Builds a signed, sideloadable Slate PDF installer (MSIX) into .\dist.

.DESCRIPTION
    - Finds MSBuild via vswhere (requires Visual Studio 2022).
    - Creates (or reuses) a self-signed signing certificate whose subject
      matches the package manifest's Publisher (CN=SlatePdf), exports the
      public .cer for recipients, and signs the package with it.
    - Builds a Release, self-contained MSIX — recipients need nothing
      preinstalled except trusting the certificate once.

.EXAMPLE
    .\build-installer.ps1                # x64 Release
    .\build-installer.ps1 -Platform ARM64
#>
param(
    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",
    [string]$Configuration = "Release",
    [string]$CertSubject = "CN=SlatePdf",
    [string]$CertPassword = "slatepdf"
)

$ErrorActionPreference = "Stop"

# ---- locate MSBuild -------------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found - is Visual Studio 2022 installed?"
}
$msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
    Select-Object -First 1
if (-not $msbuild) {
    throw "MSBuild not found via vswhere."
}

# ---- signing certificate --------------------------------------------------
# Subject must match the Publisher in Package.appxmanifest.
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $CertSubject } |
    Select-Object -First 1
if (-not $cert) {
    Write-Host "Creating self-signed signing certificate $CertSubject..."
    $cert = New-SelfSignedCertificate -Type Custom -Subject $CertSubject `
        -KeyUsage DigitalSignature -FriendlyName "Slate PDF sideload signing" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
}

$dist = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$pfx = Join-Path $PSScriptRoot "slatepdf-signing.pfx"   # gitignored - do not share
$cer = Join-Path $dist "SlatePdf.cer"                    # share this with recipients
$securePassword = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $securePassword | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null

# ---- build ----------------------------------------------------------------
& $msbuild (Join-Path $PSScriptRoot "PdfReader.sln") `
    /restore `
    /p:Configuration=$Configuration `
    /p:Platform=$Platform `
    /p:UapAppxPackageBuildMode=SideloadOnly `
    /p:AppxBundle=Never `
    /p:GenerateAppxPackageOnBuild=true `
    /p:AppxPackageSigningEnabled=true `
    /p:PackageCertificateKeyFile=$pfx `
    /p:PackageCertificatePassword=$CertPassword `
    /p:AppxPackageDir="$dist\"
if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

Write-Host ""
Write-Host "Done. Ship the contents of .\dist to recipients:" -ForegroundColor Green
Write-Host "  1. They install SlatePdf.cer once:  right-click -> Install Certificate"
Write-Host "     -> Local Machine -> 'Place all certificates in the following store'"
Write-Host "     -> Trusted People. (Admin PowerShell alternative:"
Write-Host "     Import-Certificate -FilePath SlatePdf.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople)"
Write-Host "  2. They double-click the .msix and click Install."
Write-Host ""
Write-Host "Keep slatepdf-signing.pfx private - it is your signing key." -ForegroundColor Yellow
