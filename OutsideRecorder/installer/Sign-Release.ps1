param([string]$FilePath, [switch]$CheckConfiguration)
$ErrorActionPreference = 'Stop'
$env:PSModulePath = "$PSHOME/Modules;$env:PSModulePath"
Import-Module "$PSHOME/Modules/Microsoft.PowerShell.Utility" -Force
Import-Module "$PSHOME/Modules/Microsoft.PowerShell.Security" -Force

# The private key stays in the Windows certificate store/token provider. No PFX passwords in scripts or arguments.
function Get-RecorderSignTool {
    if ($env:RECORDER_SIGNTOOL) {
        if (!(Test-Path -LiteralPath $env:RECORDER_SIGNTOOL -PathType Leaf)) { throw 'RECORDER_SIGNTOOL does not name an existing SignTool.exe.' }
        return $env:RECORDER_SIGNTOOL
    }
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $found = @(Get-ChildItem -LiteralPath $sdk -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    if ($found.Count) { return $found[0] }
    throw 'Install the Windows SDK signing tools, or set RECORDER_SIGNTOOL to SignTool.exe. No tools are installed automatically.'
}

function Assert-RecorderSignature([string]$Path, [string]$Thumbprint) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') { throw "Signature is not trusted on this PC: $Path ($($signature.Status))." }
    if (!$signature.TimeStamperCertificate) { throw "Missing trusted timestamp: $Path" }
    if ($Thumbprint -and $signature.SignerCertificate.Thumbprint -ne $Thumbprint) { throw "Unexpected signing identity: $Path" }
    $signTool = Get-RecorderSignTool
    & $signTool verify /pa /all /tw $Path | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed: $Path" }
    return $signature
}

function Assert-RecorderSigningConfiguration {
    if ($env:RECORDER_SIGNING_THUMBPRINT -notmatch '^[A-Fa-f0-9]{40}$') {
        throw 'A public release needs RECORDER_SIGNING_THUMBPRINT: the verified code-signing certificate in CurrentUser/My. Unsigned test builds remain available.'
    }
    $null = Get-RecorderSignTool
    $certificate = Get-Item -LiteralPath "Cert:/CurrentUser/My/$env:RECORDER_SIGNING_THUMBPRINT" -ErrorAction Stop
    if (!$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
        throw 'The signing certificate needs an accessible private key and must be currently valid.'
    }
    if (!(@($certificate.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId.Value }) -contains '1.3.6.1.5.5.7.3.3')) {
        throw 'The selected certificate is not a code-signing certificate.'
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Assert-RecorderSigningConfiguration
    if (!$CheckConfiguration) {
        if (!(Test-Path -LiteralPath $FilePath -PathType Leaf)) { throw 'Provide an existing file to sign.' }
        $timestamp = if ($env:RECORDER_TIMESTAMP_URL) { $env:RECORDER_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
        $timestampUri = $null
        if (![Uri]::TryCreate($timestamp, [UriKind]::Absolute, [ref]$timestampUri) -or $timestampUri.Scheme -notin @('http', 'https')) {
            throw 'RECORDER_TIMESTAMP_URL must be an absolute RFC 3161 HTTP(S) endpoint.'
        }
        $signTool = Get-RecorderSignTool
        & $signTool sign /sha1 $env:RECORDER_SIGNING_THUMBPRINT /s My /fd SHA256 /tr $timestamp /td SHA256 /d 'FFACiv Recorder' /du 'https://ffaciv.com' $FilePath | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Signing or timestamping failed: $FilePath" }
        $null = Assert-RecorderSignature $FilePath $env:RECORDER_SIGNING_THUMBPRINT
    }
}
