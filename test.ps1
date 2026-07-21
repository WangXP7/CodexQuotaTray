[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDirectory = Join-Path $projectRoot 'test-results'
$testExecutable = Join-Path $outputDirectory 'SmokeTest.exe'
$previewFile = Join-Path $outputDirectory 'popup-preview.png'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$sources = @()
$sources += Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' -File |
    Sort-Object Name |
    Select-Object -ExpandProperty FullName
$sources += Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' -File |
    Sort-Object Name |
    Select-Object -ExpandProperty FullName

$arguments = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/optimize+',
    '/warn:4',
    '/langversion:5',
    '/utf8output',
    '/main:CodexQuotaTray.Tests.SmokeTest',
    ('/out:' + $testExecutable),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll'
)
$arguments += $sources

& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Test build failed with exit code $LASTEXITCODE."
}

if (-not $env:CODEX_HOME) {
    $defaultCodexHome = Join-Path $HOME '.codex'
    if (Test-Path -LiteralPath $defaultCodexHome) {
        $env:CODEX_HOME = $defaultCodexHome
    }
}

& $testExecutable $previewFile
if ($LASTEXITCODE -ne 0) {
    throw "Smoke test failed with exit code $LASTEXITCODE."
}
