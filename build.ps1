[CmdletBinding()]
param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDirectory = Join-Path $projectRoot 'src'
$outputDirectory = Join-Path $projectRoot 'dist'
$outputFile = Join-Path $outputDirectory 'CodexQuotaTray.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework C# compiler was not found.'
}

if ($Clean -and (Test-Path -LiteralPath $outputDirectory)) {
    Get-ChildItem -LiteralPath $outputDirectory -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$sources = Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File |
    Sort-Object Name |
    Select-Object -ExpandProperty FullName

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/warn:4',
    '/langversion:5',
    '/utf8output',
    ('/out:' + $outputFile),
    ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll'
)
$arguments += $sources

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$file = Get-Item -LiteralPath $outputFile
Write-Host ('Built: ' + $file.FullName)
Write-Host ('Size:  ' + [Math]::Round($file.Length / 1KB, 1) + ' KB')
