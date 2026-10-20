param([string]$Version = '0.2.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a numeric release version.' }
& (Join-Path $PSScriptRoot 'build.ps1')
$dist = Join-Path $PSScriptRoot 'dist'
$name = "HuntersWorkshop-$Version-win-x64"
$staging = Join-Path $dist ($name+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\HuntersWorkshop.exe') -Destination $staging
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RELEASE_README.txt') -Destination (Join-Path $staging 'README.txt')
foreach ($file in @('HuntersWorkshop.exe.config','LICENSE','THIRD_PARTY_NOTICES.md','ASSETS.md','CHANGELOG.md','TESTING.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $staging
}
$zip = Join-Path $dist ($name+'.zip')
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$exeHash = (Get-FileHash -LiteralPath (Join-Path $staging 'HuntersWorkshop.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
@("$hash  $name.zip", "$exeHash  HuntersWorkshop.exe") | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII
Write-Output "Release: $zip"
Write-Output "SHA256: $hash"

