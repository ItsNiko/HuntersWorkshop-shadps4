param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
if (!$OutputPath) { $OutputPath = Join-Path $PSScriptRoot 'bin\HuntersWorkshop.exe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path $OutputPath) -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework 4.8 compiler is required.' }
$framework = Split-Path $compiler
$wpf = Join-Path $framework 'WPF'
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/codepage:65001','/warn:4','/warnaserror+',('/out:'+$OutputPath),('/win32manifest:'+(Join-Path $PSScriptRoot 'src\app.manifest')),('/win32icon:'+(Join-Path $PSScriptRoot 'assets\HuntersWorkshop.ico')), '/r:System.Web.Extensions.dll','/r:System.Core.dll','/r:System.Management.dll','/r:System.Windows.Forms.dll',('/r:'+(Join-Path $wpf 'PresentationFramework.dll')),('/r:'+(Join-Path $wpf 'PresentationCore.dll')),('/r:'+(Join-Path $wpf 'WindowsBase.dll')),('/r:'+(Join-Path $framework 'System.Xaml.dll')))
foreach ($resource in @('src\Main.xaml','src\Theme.xaml','data\catalog.json','data\profile.json','assets\emblem.png','assets\workshop.png','assets\HuntersWorkshop.ico')) {
 $arguments += '/resource:'+(Join-Path $PSScriptRoot $resource)+','+[IO.Path]::GetFileName($resource)
}
$arguments += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | Sort-Object Name | Select-Object -ExpandProperty FullName
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output ('Built '+$OutputPath)
