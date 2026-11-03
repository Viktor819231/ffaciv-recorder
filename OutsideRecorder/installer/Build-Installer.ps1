# -GitHub: the build GitHub makes from the public source for its Releases page; unsigned (the owner chose no code
# signing), so the setup file carries the plain public name instead of "-UNSIGNED-TEST".
param([switch]$Release, [switch]$Scan, [switch]$GitHub)
$ErrorActionPreference = 'Stop'
# A Git Bash launched by PowerShell 7 can inherit its incompatible module path into Windows PowerShell 5.1.
$env:PSModulePath = "$PSHOME/Modules;$env:PSModulePath"
Import-Module "$PSHOME/Modules/Microsoft.PowerShell.Utility" -Force
$repo = (Resolve-Path -LiteralPath "$PSScriptRoot/../..").Path
$buildRoot = Join-Path $repo 'out/or04'
$app = Join-Path $buildRoot 'app'
$innoVersion = '7.1.0'
$innoHash = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
$innoDirectory = Join-Path $repo "out/tools/innosetup-$innoVersion"
$compiler = Join-Path $innoDirectory 'ISCC.exe'
$project = Join-Path $repo 'OutsideRecorder/OutsideRecorder.Shell/OutsideRecorder.Shell.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'The recorder version must be x.y.z.' }
if ($Release) {
    & "$PSScriptRoot/Sign-Release.ps1" -CheckConfiguration
    if (!$?) { throw 'Signing configuration check failed.' }
}
$null = New-Item -ItemType Directory -Path $buildRoot -Force
# Resolve the exact target before recursive cleanup; never follow an app-folder junction.
if (Test-Path -LiteralPath $app) {
    $item = Get-Item -LiteralPath $app -Force
    if ($item.FullName -ne [IO.Path]::GetFullPath($app) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to clean an unexpected or linked app directory.'
    }
    Remove-Item -LiteralPath $item.FullName -Recurse -Force
}
$suffix = if ($Release -or $GitHub) { '' } else { '-UNSIGNED-TEST' }
$setupName = "FFACiv-Recorder-Setup-$version$suffix"
$setup = Join-Path $buildRoot "$setupName.exe"
foreach ($name in @("$setupName.exe", 'release-manifest.json', 'SHA256SUMS.txt', 'defender-scan.json')) {
    $old = Join-Path $buildRoot $name
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old }
}
# The FFACiv project's dn.sh sets the NuGet variables its PC needs; anywhere else (the public repository, GitHub's
# build machine) plain dotnet does the same job.
if (Test-Path -LiteralPath (Join-Path $repo 'dn.sh') -PathType Leaf) {
    $bash = if ($env:RECORDER_BASH) { $env:RECORDER_BASH } else { Join-Path $env:ProgramFiles 'Git/bin/bash.exe' }
    if (!(Test-Path -LiteralPath $bash -PathType Leaf)) { throw 'Git Bash is required. Set RECORDER_BASH to its bash.exe.' }
    $dotnet = $bash; $dotnetArgs = @('./dn.sh')
} else { $dotnet = 'dotnet'; $dotnetArgs = @() }
Push-Location $repo
try {
    # Both test and signed installers use public restrictions; plain dn.sh publish keeps developer tools.
    & $dotnet @dotnetArgs publish OutsideRecorder/OutsideRecorder.Shell -c Release -o out/or04/app '-p:RecorderPublicRelease=true' "-p:Version=$version"
    if ($LASTEXITCODE -ne 0) { throw 'Recorder publish failed.' }
    & $dotnet @dotnetArgs run OutsideRecorder/installer/Verify-PlayerBuild.cs -- out/or04/app
    if ($LASTEXITCODE -ne 0) { throw 'Developer tools found in player build.' }
    # Carry the exact dependency licenses/notices from the packages used by this publish.
    $assets = Get-Content -LiteralPath "$repo/OutsideRecorder/OutsideRecorder.Shell/obj/project.assets.json" -Raw | ConvertFrom-Json
    $webview = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -like 'Microsoft.Web.WebView2/*' })
    if ($webview.Count -ne 1) { throw 'Cannot identify the WebView2 dependency for its notices.' }
    $packages = @($webview[0].Name.ToLowerInvariant())
    foreach ($id in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
        $dependency = @($assets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object { $_.name -eq $id })
        $versions = @([regex]::Matches(($dependency.version -join ' '), '\d+\.\d+\.\d+(?:-[\w.]+)?') | ForEach-Object { $_.Value } | Select-Object -Unique)
        if ($versions.Count -ne 1) { throw "Cannot identify $id version for its notices." }
        $packages += "$($id.ToLowerInvariant())/$($versions[0])"
    }
    foreach ($package in $packages) {
        $locations = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $package } | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
        if (!$locations.Count) { throw "Cannot locate dependency notices: $package" }
        $notices = @(Get-ChildItem -LiteralPath $locations[0] -File | Where-Object { $_.Name -match '^(LICENSE(?:\.TXT)?|NOTICE\.txt|THIRD-PARTY-NOTICES\.TXT)$' })
        if (!($notices | Where-Object { $_.Name -like 'LICENSE*' })) { throw "Missing license: $package" }
        $destination = Join-Path $app "notices/$package"
        $null = New-Item -ItemType Directory -Path $destination -Force
        $notices | Copy-Item -Destination $destination
    }
    Copy-Item -LiteralPath "$repo/OutsideRecorder/release/READ-ME.txt", "$repo/OutsideRecorder/release/PRIVACY.txt" -Destination $app
    $notes = Join-Path $repo "OutsideRecorder/release/RELEASE-NOTES-$version.txt"
    if (!(Test-Path -LiteralPath $notes)) { throw "Write release notes for $version before packaging." }
    Copy-Item -LiteralPath $notes -Destination $app
    if (!(Test-Path -LiteralPath $compiler)) {
        $null = New-Item -ItemType Directory -Path $innoDirectory -Force
        $bootstrap = Join-Path $buildRoot "innosetup-$innoVersion.exe"
        Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/jrsoftware/issrc/releases/download/is-$($innoVersion.Replace('.','_'))/innosetup-$innoVersion-x64.exe" -OutFile $bootstrap -TimeoutSec 120
        if ((Get-FileHash -LiteralPath $bootstrap -Algorithm SHA256).Hash -ne $innoHash) { throw 'Inno Setup download checksum mismatch.' }
        $process = Start-Process -FilePath $bootstrap -WindowStyle Hidden -Wait -PassThru -ArgumentList @(
            '/PORTABLE=1', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/CURRENTUSER', '/NORESTART', '/NOICONS', "/DIR=`"$innoDirectory`"")
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw 'Portable Inno Setup extraction failed.' }
        Remove-Item -LiteralPath $bootstrap
    }
    $arguments = @('/Qp', "/DAppVersion=$version", "/DSourceDir=$app", "/DOutputDir=$buildRoot", "/DSetupFilename=$setupName")
    if ($Release) {
        foreach ($file in @('FFACivRecorder.exe', 'FFACivRecorder.dll', 'OutsideRecorder.Core.dll')) {
            & "$PSScriptRoot/Sign-Release.ps1" -FilePath (Join-Path $app $file)
            if (!$?) { throw "Signing failed: $file" }
        }
        # Inno signs Setup, temporary executable copies and the uninstaller, not just the outer EXE.
        $powershell = Join-Path $PSHOME 'powershell.exe'
        $signScript = Join-Path $PSScriptRoot 'Sign-Release.ps1'
        $signCommand = '$q' + $powershell + '$q -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $q' + $signScript + '$q -FilePath $f'
        $arguments += @('/DRecorderSigned', "/SRecorderSign=$signCommand")
    }
    $arguments += Join-Path $PSScriptRoot 'FFACivRecorder.iss'
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation/signing failed.' }
    & "$PSScriptRoot/Test-Package.ps1" -AppDirectory $app -Installer $setup -Version $version -OutputDirectory $buildRoot -RequireSigned:$Release -Scan:($Release -or $Scan)
    if (!$?) { throw 'Package validation failed.' }
    Write-Host "Prepared locally: $setup"
    if ($GitHub) { Write-Host 'GitHub build: unsigned, made from the public source. No files have been published.' }
    elseif (!$Release) { Write-Warning 'UNSIGNED TEST BUILD. Not approved for public release. No files have been published.' }
} finally { Pop-Location }
