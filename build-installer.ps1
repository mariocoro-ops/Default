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

# ---- bundle a single shareable zip ----------------------------------------
# MSBuild emits a "<name>_<version>_<platform>_Test" folder containing the
# signed .msix, its dependencies, the .cer, and Microsoft's generated
# Add-AppDevPackage.ps1 one-click installer. Zip that folder so there is one
# artifact to hand out.
$testFolder = Get-ChildItem -Path $dist -Directory -Filter "*_Test" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $testFolder) {
    throw "Could not find the generated sideload package folder under $dist."
}

# Drop a plain-English note for the recipient next to the installer script.
$readme = @"
Slate PDF - install

1. Right-click 'Add-AppDevPackage.ps1' -> Run with PowerShell.
   (It trusts the signing certificate, then installs the app. Approve the
   admin prompt.) If PowerShell is blocked, run this once in an admin
   PowerShell window from this folder:
       Set-ExecutionPolicy -Scope Process Bypass -Force; .\Add-AppDevPackage.ps1

2. 'Slate PDF' then appears in the Start menu and as a handler for .pdf files.

Manual alternative: install SlatePdf.cer into Local Machine > Trusted People,
then double-click the .msix.
"@
Set-Content -Path (Join-Path $testFolder.FullName "INSTALL.txt") -Value $readme -Encoding UTF8

$zipPath = Join-Path $dist ("$($testFolder.Name -replace '_Test$','')-installer.zip")
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $testFolder.FullName '*') -DestinationPath $zipPath

Write-Host ""
Write-Host "Installer ready:" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host ""
Write-Host "Send that zip. The recipient extracts it and either:" -ForegroundColor Green
Write-Host "  * right-clicks Add-AppDevPackage.ps1 -> Run with PowerShell (one-click), or"
Write-Host "  * installs SlatePdf.cer into Local Machine > Trusted People, then"
Write-Host "    double-clicks the .msix."
Write-Host ""
Write-Host "To install on THIS machine right now, run that same"
Write-Host "Add-AppDevPackage.ps1 from:" -ForegroundColor Green
Write-Host "  $($testFolder.FullName)"
