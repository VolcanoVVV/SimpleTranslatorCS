param([string]$OtherProject = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $taskRoot 'Shared/manifest.json') -Raw | ConvertFrom-Json
function Get-PortableSourceHash([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $text = [Text.Encoding]::UTF8.GetString($bytes).Replace("`r`n", "`n")
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-', '') }
    finally { $hash.Dispose() }
}
foreach ($entry in $manifest.files.PSObject.Properties) {
    $path = Join-Path $taskRoot "Shared/$($entry.Name)"
    if ((Get-PortableSourceHash $path) -ne $entry.Value) { throw "Foundation changed without updating the manifest: $($entry.Name)" }
    if ($OtherProject -and (Get-PortableSourceHash (Join-Path $OtherProject "Shared/$($entry.Name)")) -ne $entry.Value) {
        throw "Foundation differs between projects: $($entry.Name)"
    }
}
Write-Output 'PASS: portable translation foundation hashes.'
