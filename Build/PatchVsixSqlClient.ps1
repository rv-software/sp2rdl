param(
    [Parameter(Mandatory = $true)]
    [string] $VsixPath,

    [Parameter(Mandatory = $true)]
    [string] $RuntimeAssemblyPath,

    [Parameter(Mandatory = $true)]
    [string] $SniAssemblyPath
)

$ErrorActionPreference = "Stop"

$vsixExists = Test-Path -LiteralPath $VsixPath
$runtimeAssemblyExists = Test-Path -LiteralPath $RuntimeAssemblyPath
$sniAssemblyExists = Test-Path -LiteralPath $SniAssemblyPath

if (-not $vsixExists -or -not $runtimeAssemblyExists -or -not $sniAssemblyExists) {
    return
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::Open($VsixPath, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    $existingEntry = $zip.GetEntry("System.Data.SqlClient.dll")
    if ($null -ne $existingEntry) {
        $existingEntry.Delete()
    }

    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip,
        $RuntimeAssemblyPath,
        "System.Data.SqlClient.dll",
        [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null

    $existingSniEntry = $zip.GetEntry("sni.dll")
    if ($null -ne $existingSniEntry) {
        $existingSniEntry.Delete()
    }

    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip,
        $SniAssemblyPath,
        "sni.dll",
        [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
finally {
    $zip.Dispose()
}
