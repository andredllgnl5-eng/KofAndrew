param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ClientPath = 'C:\Users\Abyss\OneDrive\Desktop\KofHostOnline\KofClient',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\release-output')
)

$ErrorActionPreference = 'Stop'
$tag = 'v' + $Version.TrimStart('v')
$versionNumber = $tag.TrimStart('v')
$assetName = "KofAndrew-Client-$tag.zip"
$archive = Join-Path $OutputPath $assetName
$manifestPath = Join-Path $OutputPath 'latest.json'
$baseUri = New-Object System.Uri($ClientPath.TrimEnd('\') + '\')

function Get-RelativePath([string]$Path) {
    [Uri]::UnescapeDataString($baseUri.MakeRelativeUri((New-Object System.Uri($Path))).ToString())
}

New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
if (Test-Path $archive) { Remove-Item -LiteralPath $archive -Force }

$managed = Get-ChildItem -LiteralPath $ClientPath -File -Recurse | Where-Object {
    $relative = Get-RelativePath $_.FullName
    $relative -ne 'launcher-config.json' -and
    $relative -ne 'client-version.json' -and
    -not $relative.StartsWith('game/save/')
}

$entries = New-Object System.Collections.Generic.List[object]
foreach ($file in $managed) {
    $entries.Add([ordered]@{
        path = Get-RelativePath $file.FullName
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    })
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $managed) {
        $entry = $zip.CreateEntry((Get-RelativePath $file.FullName), [IO.Compression.CompressionLevel]::Fastest)
        $input = $file.OpenRead()
        $output = $entry.Open()
        try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
    }
} finally { $zip.Dispose() }

$packageHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
$manifest = [ordered]@{
    version = $versionNumber
    publishedAt = (Get-Date).ToUniversalTime().ToString('o')
    packageUrl = "https://github.com/andredllgnl5-eng/KofAndrew-Updates/releases/download/$tag/$assetName"
    packageSha256 = $packageHash
    files = $entries
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

gh release create $tag $archive --repo andredllgnl5-eng/KofAndrew-Updates --title "KOF Andrew Online $tag" --notes "Atualização oficial do KOF Andrew Online. Made By Andrew."
$updatesRepo = Join-Path $OutputPath 'updates-repo'
if (-not (Test-Path (Join-Path $updatesRepo '.git'))) {
    gh repo clone andredllgnl5-eng/KofAndrew-Updates $updatesRepo
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $updatesRepo 'latest.json') -Force
Push-Location $updatesRepo
try {
    git pull --ff-only
    git add -- latest.json
    git commit -m "Publicar manifesto $tag"
    git push
} finally { Pop-Location }
Write-Host "Release e manifesto $tag publicados com sucesso."
