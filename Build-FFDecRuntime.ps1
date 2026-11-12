[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DestinationDirectory
)

$ErrorActionPreference = "Stop"

$JavaMajorVersion = 21
$MetadataUri = "https://api.azul.com/metadata/v1/zulu/packages/?java_version=$JavaMajorVersion&os=windows&arch=x64&java_package_type=jre&javafx_bundled=false&archive_type=zip&release_status=ga&availability_types=CA&latest=true&page=1&page_size=20"
$DestinationDirectory = [System.IO.Path]::GetFullPath($DestinationDirectory)
$BundledJava = Join-Path $DestinationDirectory "bin\java.exe"

if (Test-Path -LiteralPath $BundledJava -PathType Leaf) {
    Write-Host "Bundled Java runtime already exists: $BundledJava"
    exit 0
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$TemporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "VibeSkua-FFDec-Java-$([Guid]::NewGuid().ToString('N'))"
$ArchivePath = Join-Path $TemporaryDirectory "zulu-jre.zip"
$ExtractionDirectory = Join-Path $TemporaryDirectory "Extracted"

try {
    New-Item -ItemType Directory -Path $TemporaryDirectory -Force | Out-Null

    Write-Host "Resolving the latest Azul Zulu Java $JavaMajorVersion x64 JRE package..."
    $Packages = @(Invoke-RestMethod -Uri $MetadataUri -Method Get -Headers @{ Accept = "application/json" } -TimeoutSec 60)
    $Package = $Packages |
        Where-Object {
            $_.java_version.Count -gt 0 -and
            $_.java_version[0] -eq $JavaMajorVersion -and
            $_.name -match "win_x64\.zip$" -and
            $_.package_uuid -and
            $_.download_url -match "^https://"
        } |
        Select-Object -First 1

    if ($null -eq $Package) {
        throw "Azul did not return a Java $JavaMajorVersion Windows x64 JRE ZIP package. Metadata endpoint: $MetadataUri"
    }

    $PackageDetailsUri = "https://api.azul.com/metadata/v1/zulu/packages/$($Package.package_uuid)"
    $PackageDetails = Invoke-RestMethod -Uri $PackageDetailsUri -Method Get -Headers @{ Accept = "application/json" } -TimeoutSec 60
    $ExpectedHash = [string]$PackageDetails.sha256_hash
    if ($ExpectedHash -notmatch "^[a-fA-F0-9]{64}$") {
        throw "Azul did not provide a valid SHA-256 checksum for package $($Package.name)."
    }

    Write-Host "Downloading $($Package.name)..."
    Invoke-WebRequest -Uri $Package.download_url -OutFile $ArchivePath -UseBasicParsing -TimeoutSec 900

    $ActualHash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
    if (-not [string]::Equals($ActualHash, $ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Java runtime checksum verification failed. Expected $ExpectedHash, received $ActualHash."
    }

    Write-Host "Java runtime archive SHA-256 verified. Extracting..."
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $ExtractionDirectory -Force

    $JavaExecutable = Get-ChildItem -LiteralPath $ExtractionDirectory -Filter "java.exe" -File -Recurse |
        Where-Object { $_.Directory.Name -eq "bin" } |
        Select-Object -First 1

    if ($null -eq $JavaExecutable) {
        throw "The verified Azul archive did not contain bin\java.exe."
    }

    $JavaHome = Split-Path -Parent (Split-Path -Parent $JavaExecutable.FullName)
    $VersionOutput = (& $JavaExecutable.FullName -version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $VersionOutput -notmatch 'version "21(?:\.|")') {
        throw "The bundled Java runtime failed its version check. Output: $VersionOutput"
    }

    if (Test-Path -LiteralPath $DestinationDirectory) {
        Remove-Item -LiteralPath $DestinationDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $JavaHome -Force |
        Copy-Item -Destination $DestinationDirectory -Recurse -Force

    if (-not (Test-Path -LiteralPath $BundledJava -PathType Leaf)) {
        throw "Java extraction completed without staging the expected runtime: $BundledJava"
    }

    @"
Distribution: Azul Zulu Community JRE
Java major version: $JavaMajorVersion
Package: $($Package.name)
SHA-256: $ActualHash
Download: $($Package.download_url)
"@ | Set-Content -LiteralPath (Join-Path $DestinationDirectory "VibeSkua-Runtime-Info.txt") -Encoding UTF8

    Write-Host "Bundled Java runtime ready: $BundledJava"
    Write-Host $VersionOutput
}
finally {
    if (Test-Path -LiteralPath $TemporaryDirectory) {
        Remove-Item -LiteralPath $TemporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
