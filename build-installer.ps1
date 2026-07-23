<#
.SYNOPSIS
    Builds a signed, sideloadable Slate PDF installer (MSIX) into .\dist.

.DESCRIPTION
    - Finds MSBuild via vswhere (requires Visual Studio 2022).
    - Creates (or reuses) a self-signed signing certificate in the current
      user's certificate store whose subject matches the package manifest's
      Publisher (CN=SlatePdf), and exports the public .cer for recipients.
    - Signs by certificate thumbprint straight from the store — the same
      mechanism Visual Studio uses. (Passing a password-protected .pfx file
      to MSBuild trips APPX0105 on many setups, so we don't.)
    - Builds a Release, self-contained MSIX — recipients need nothing
      preinstalled except trusting the certificate once.

    The signing key stays in your user certificate store (certmgr.msc →
    Personal → Certificates → CN=SlatePdf). Export it as a .pfx from there if
    you ever need to build on another machine with the same identity.

.EXAMPLE
    .\build-installer.ps1                # x64 Release
    .\build-installer.ps1 -Platform ARM64
#>
param(
    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",
    [string]$Configuration = "Release",
    [string]$CertSubject = "CN=SlatePdf"
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
    Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $cert) {
    Write-Host "Creating self-signed signing certificate $CertSubject..."
    $cert = New-SelfSignedCertificate -Type Custom -Subject $CertSubject `
        -KeyUsage DigitalSignature -FriendlyName "Slate PDF sideload signing" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
}
Write-Host "Signing with certificate $($cert.Thumbprint) ($CertSubject)"

$dist = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Public certificate for recipients to trust.
$cer = Join-Path $dist "SlatePdf.cer"
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
    /p:PackageCertificateThumbprint=$($cert.Thumbprint) `
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
