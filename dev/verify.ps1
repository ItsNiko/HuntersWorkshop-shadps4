param([switch]$SkipPython, [switch]$SkipUI)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
& (Join-Path $root 'build.ps1')
$output = Join-Path $root 'test-output'
$exe = Join-Path $root 'bin\HuntersWorkshop.exe'
function Invoke-AppCheck([string]$Mode, [string]$Directory) {
    $process = Start-Process -FilePath $exe -ArgumentList $Mode,('"'+$Directory+'"') -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "$Mode failed with exit code $($process.ExitCode). Inspect $Directory and the application-data error log." }
}
Invoke-AppCheck '--self-test' (Join-Path $output 'core')
Get-Content -LiteralPath (Join-Path $output 'core\self-tests.txt')
if (!$SkipPython) {
    & python (Join-Path $PSScriptRoot 'test_bridge.py') (Join-Path $output 'core')
    if ($LASTEXITCODE -ne 0) { throw 'Unicorn bridge checks failed. Install dev/requirements.txt.' }
    & python (Join-Path $PSScriptRoot 'test_native_bridge.py') (Join-Path $output 'core')
    if ($LASTEXITCODE -ne 0) { throw 'Native CPU bridge checks failed.' }
}
if (!$SkipUI) {
    Invoke-AppCheck '--ui-test' (Join-Path $output 'ui')
    Get-Content -LiteralPath (Join-Path $output 'ui\ui-tests.txt')
}

