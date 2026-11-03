param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Sign-Release.ps1"
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$original = Get-Content -LiteralPath (Join-Path $package 'release-manifest.json') -Raw | ConvertFrom-Json
if ($original.releaseReady) { throw 'Use an unsigned test package for these negative checks.' }
$repo = (Resolve-Path -LiteralPath "$PSScriptRoot/../..").Path
$scratch = Join-Path $repo ('out/release-pipeline-test-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$app = Join-Path $scratch 'app'
Copy-Item -LiteralPath (Join-Path $package 'app') -Destination $app -Recurse
$setup = Join-Path $package $original.installer.file
$report = Join-Path $scratch 'reports'
$parameters = @{ AppDirectory = $app; Installer = $setup; Version = $original.version; OutputDirectory = $report }
function Expect-Rejection([string]$Expected, [scriptblock]$Action) {
    $caught = $false
    try { & $Action } catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
        $caught = $true
    }
    if (!$caught) { throw "Expected rejection: $Expected" }
    if (Test-Path -LiteralPath (Join-Path $report 'release-manifest.json')) { throw 'A rejected package retained a passing manifest.' }
}
& "$PSScriptRoot/Test-Package.ps1" @parameters
$manifest = Get-Content -LiteralPath (Join-Path $report 'release-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.releaseReady) { throw 'Unsigned package incorrectly marked release-ready.' }
$actual = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.installer.sha256 -ne $actual) { throw 'Installer checksum mismatch.' }
foreach ($file in $manifest.files) {
    if ((Get-FileHash -LiteralPath (Join-Path $app $file.file) -Algorithm SHA256).Hash -ne $file.sha256) { throw "Inventory mismatch: $($file.file)" }
}
Expect-Rejection 'Signature is not trusted' { & "$PSScriptRoot/Test-Package.ps1" @parameters -RequireSigned -Scan }
# Simulate stale reports and an accidentally shipped debug artifact.
$null = New-Item -ItemType Directory -Path $report -Force
'{"releaseReady":true}' | Set-Content -LiteralPath (Join-Path $report 'release-manifest.json')
$debugFile = Join-Path $app 'accident.pdb'
'not for distribution' | Set-Content -LiteralPath $debugFile
Expect-Rejection 'Development files, credentials or recordings' { & "$PSScriptRoot/Test-Package.ps1" @parameters }
Remove-Item -LiteralPath $debugFile
$parameters.Version = '99.0.0'
Expect-Rejection 'Version mismatch' { & "$PSScriptRoot/Test-Package.ps1" @parameters }
Write-Host 'Release pipeline checks passed: unsigned rejection, leaked-file rejection, stale-report removal, version guard and exact file checksums.'
