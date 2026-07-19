[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,

    [string]$ExpectedVersion = '0.1.0',

    [string]$ExpectedRepositoryUrl = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$expectedIds = @(
    'Industrial.Communication.Abstractions',
    'Industrial.Communication.Adapters',
    'Industrial.Communication.Core',
    'Industrial.Communication.DependencyInjection',
    'Industrial.Communication.Protocols.Mc',
    'Industrial.Communication.Protocols.Modbus',
    'Industrial.Communication.Protocols.OpcUa',
    'Industrial.Communication.Protocols.S7',
    'Industrial.Communication.Transports'
)
$resolvedDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packages = @(Get-ChildItem -LiteralPath $resolvedDirectory -Filter '*.nupkg' -File |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } | Sort-Object Name)
$symbols = @(Get-ChildItem -LiteralPath $resolvedDirectory -Filter '*.snupkg' -File | Sort-Object Name)

if ($packages.Count -ne $expectedIds.Count) {
    throw "Expected $($expectedIds.Count) nupkg files, found $($packages.Count)."
}
if ($symbols.Count -ne $expectedIds.Count) {
    throw "Expected $($expectedIds.Count) snupkg files, found $($symbols.Count)."
}

foreach ($package in $packages) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName })
        $nuspecEntry = $archive.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
        if ($null -eq $nuspecEntry) {
            throw "$($package.Name) has no nuspec."
        }

        $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
        try {
            [xml]$nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        $id = $metadata.SelectSingleNode("*[local-name()='id']").InnerText
        $version = $metadata.SelectSingleNode("*[local-name()='version']").InnerText
        if ($expectedIds -notcontains $id) {
            throw "Unexpected package id '$id'."
        }
        if ($version -ne $ExpectedVersion) {
            throw "$id version is '$version', expected '$ExpectedVersion'."
        }

        foreach ($required in @('README.md', 'LICENSE', 'CHANGELOG.md', 'package-icon.png')) {
            if ($names -notcontains $required) {
                throw "$id does not contain $required."
            }
        }

        $libraries = @($names | Where-Object { $_ -match '^lib/.+\.dll$' })
        if ($libraries.Count -ne 1 -or $libraries[0] -notmatch '^lib/netstandard2\.1/') {
            throw "$id must contain exactly one netstandard2.1 library asset. Found: $($libraries -join ', ')"
        }
        if (-not ($names | Where-Object { $_ -match '^lib/netstandard2\.1/.+\.xml$' })) {
            throw "$id does not contain its XML documentation file."
        }
        if ($names | Where-Object { $_ -match '(^|/)(tests?|samples?|TestData)(/|$)|\.(pfx|p12|pem|key|cer)$|appsettings' }) {
            throw "$id contains a forbidden test, sample, certificate or configuration artifact."
        }

        $license = $metadata.SelectSingleNode("*[local-name()='license']")
        if ($null -eq $license -or $license.InnerText -ne 'MIT' -or $license.GetAttribute('type') -ne 'expression') {
            throw "$id does not declare the MIT SPDX license expression."
        }
        if ($metadata.SelectSingleNode("*[local-name()='readme']").InnerText -ne 'README.md') {
            throw "$id does not declare README.md."
        }
        if ($metadata.SelectSingleNode("*[local-name()='icon']").InnerText -ne 'package-icon.png') {
            throw "$id does not declare package-icon.png."
        }

        if ($ExpectedRepositoryUrl) {
            $repository = $metadata.SelectSingleNode("*[local-name()='repository']")
            if ($null -eq $repository -or $repository.GetAttribute('url') -ne $ExpectedRepositoryUrl) {
                throw "$id repository URL does not match '$ExpectedRepositoryUrl'."
            }
        }

        $dependencyIds = @($metadata.SelectNodes(".//*[local-name()='dependency']") |
            ForEach-Object { $_.GetAttribute('id') })
        if ($dependencyIds | Where-Object { $_ -match 'xunit|Test\.Sdk|coverlet|SourceLink' }) {
            throw "$id exposes a build/test-only package dependency."
        }

        foreach ($textEntry in $archive.Entries | Where-Object {
            $_.FullName -match '\.(md|txt|xml|nuspec|json|config)$' -and $_.Length -lt 2MB
        }) {
            $textReader = [System.IO.StreamReader]::new($textEntry.Open())
            try {
                $text = $textReader.ReadToEnd()
            }
            finally {
                $textReader.Dispose()
            }
            if ($text -match '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----') {
                throw "$id contains private-key material in $($textEntry.FullName)."
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    $symbolPath = Join-Path $resolvedDirectory "$($package.BaseName).snupkg"
    if (-not (Test-Path -LiteralPath $symbolPath)) {
        throw "Missing symbol package for $($package.Name)."
    }
    $symbolArchive = [System.IO.Compression.ZipFile]::OpenRead($symbolPath)
    try {
        $symbolNames = @($symbolArchive.Entries | ForEach-Object { $_.FullName })
        if (-not ($symbolNames | Where-Object { $_ -match '\.pdb$' })) {
            throw "$id symbol package contains no portable PDB."
        }
        if ($symbolNames | Where-Object { $_ -match '\.(pfx|p12|pem|key)$' }) {
            throw "$id symbol package contains a sensitive key file."
        }
    }
    finally {
        $symbolArchive.Dispose()
    }

    Write-Host "Package OK: $id $version"
}

Write-Host "Verified $($packages.Count) NuGet packages and $($symbols.Count) symbol packages."
