param(
    [Parameter(Mandatory=$true)][string]$AppDirectory,
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$RequireSigned,
    [switch]$Scan
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Sign-Release.ps1"
$app = (Resolve-Path -LiteralPath $AppDirectory).Path
$setup = (Resolve-Path -LiteralPath $Installer).Path
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
if ($output -eq $app -or $output.StartsWith($app + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Reports must be outside the app folder.' }
# A failed rerun must not leave an older passing manifest beside new files.
foreach ($name in @('release-manifest.json', 'SHA256SUMS.txt', 'defender-scan.json')) {
    $previous = Join-Path $output $name
    if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous }
}
$files = @(Get-ChildItem -LiteralPath $app -Recurse -File -Force)
$forbidden = @($files | Where-Object {
    $_.Name -match '(?i)\.(pdb|xml|pfx|p12|pem|key|Civ5Save|civ5replay|jsonl|part)$' -or
    $_.Name -in @('settings.txt', 'games.json', 'backup.json') -or
    $_.FullName.Substring($app.Length + 1) -match '(?i)(^|[\\/])(check-tool|tools|tests|samples|memory-snapshots|ui-updates)([\\/]|$)'
})
if ($forbidden.Count) { throw "Development files, credentials or recordings in package: $($forbidden.Name -join ', ')" }
$owned = @('FFACivRecorder.exe', 'FFACivRecorder.dll', 'OutsideRecorder.Core.dll')
$signatures = @()
foreach ($name in $owned) {
    $path = Join-Path $app $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing application file: $name" }
    $details = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    if ($details.CompanyName -ne 'FFACiv') { throw "Unexpected publisher metadata: $name" }
    if ($details.ProductVersion -ne $Version) { throw "Version mismatch: $name $($details.ProductVersion), package $Version" }
}
foreach ($path in @($owned | ForEach-Object { Join-Path $app $_ }) + @($setup)) {
    $sig = if ($RequireSigned) { Assert-RecorderSignature $path $env:RECORDER_SIGNING_THUMBPRINT } else { Get-AuthenticodeSignature -LiteralPath $path }
    $signatures += [ordered]@{ file = [IO.Path]::GetFileName($path); status = $sig.Status.ToString();
        publisher = if ($sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { $null };
        thumbprint = if ($sig.SignerCertificate) { $sig.SignerCertificate.Thumbprint } else { $null };
        timestamped = [bool]$sig.TimeStamperCertificate }
}
if ($RequireSigned -and (@($signatures.thumbprint | Select-Object -Unique).Count -ne 1)) { throw 'App and installer must use the same publisher certificate.' }
$scanReport = [ordered]@{ status = 'not-run'; scope = @($app, $setup); runs = @() }
if ($Scan) {
    try {
        $status = Get-MpComputerStatus -ErrorAction Stop
        if (!$status.AntivirusEnabled) { throw 'Microsoft Defender Antivirus is not enabled.' }
        if (!$status.AntivirusSignatureLastUpdated -or ((Get-Date) - $status.AntivirusSignatureLastUpdated).TotalDays -gt 2) {
            throw 'Defender definitions are older than two days. Update security intelligence before scanning.'
        }
        $scanReport['engineVersion'] = $status.AMEngineVersion
        $scanReport['signatureVersion'] = $status.AntivirusSignatureVersion
        $scanReport['signatureUpdated'] = $status.AntivirusSignatureLastUpdated.ToUniversalTime().ToString('o')
        $platform = Join-Path $env:ProgramData 'Microsoft/Windows Defender/Platform'
        $candidates = @(Get-ChildItem -LiteralPath $platform -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
        $command = if ($candidates.Count) { $candidates[0] } else { Join-Path $env:ProgramFiles 'Windows Defender/MpCmdRun.exe' }
        foreach ($target in @($app, $setup)) {
            # DisableRemediation is per-scan: reports detections without deleting/quarantining build files;
            # it does not turn off protection or add exclusions. This scan ignores configured exclusions.
            $log = @(& $command -Scan -ScanType 3 -File $target -DisableRemediation 2>&1 | ForEach-Object { "$_" })
            $code = $LASTEXITCODE
            $scanReport.runs += [ordered]@{ path = $target; exitCode = $code; output = $log }
            if ($code -ne 0) { throw "Defender scan did not pass (exit $code): $target. See defender-scan.json." }
        }
        $scanReport.status = 'passed'
    } catch {
        $scanReport.status = 'failed-or-unavailable'
        $scanReport['error'] = $_.Exception.Message
        throw
    } finally {
        $scanReport['checkedAt'] = [DateTime]::UtcNow.ToString('o')
        $scanReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'defender-scan.json') -Encoding UTF8
    }
}
if ($RequireSigned -and !$Scan) { throw 'Public release validation requires -Scan as well as -RequireSigned.' }
$inventory = @($files | Sort-Object FullName | ForEach-Object {
    [ordered]@{ file = $_.FullName.Substring($app.Length + 1).Replace('\','/'); size = $_.Length;
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$setupFile = Get-Item -LiteralPath $setup
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$repo = (Resolve-Path -LiteralPath "$PSScriptRoot/../..").Path
$revision = & git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot record source revision.' }
$dirty = @(& git -C $repo status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'Cannot record source working-tree status.' }
$manifest = [ordered]@{ version = $Version; uiVersion = [int](Get-Content -LiteralPath (Join-Path $app 'ui/ui-version.txt') -Raw).Trim(); createdAt = [DateTime]::UtcNow.ToString('o');
    releaseReady = [bool]($RequireSigned -and $scanReport.status -eq 'passed');
    source = @{ revision = $revision; uncommittedChanges = ($dirty.Count -gt 0) };
    installer = @{ file = $setupFile.Name; size = $setupFile.Length; sha256 = $hash };
    signatures = $signatures; defender = $scanReport; files = $inventory;
    limitations = @('A clean scan is not a guarantee against other antivirus detections or SmartScreen warnings.',
        'Real-game stability and clean-PC installation must be checked separately.') }
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'release-manifest.json') -Encoding UTF8
"$hash  $($setupFile.Name)" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ASCII
Write-Host "Package checked: $($setupFile.Name); releaseReady=$($manifest.releaseReady)"
