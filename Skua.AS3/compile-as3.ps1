[CmdletBinding()]
param(
    [string]$MxmlcPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Join-Path $scriptRoot 'skua'
$sourcePath = Join-Path $projectRoot 'src'
$mainSourcePath = Join-Path $sourcePath 'skua/Main.as'
$outputDirectory = Join-Path $projectRoot 'bin'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputPath = Join-Path $outputDirectory 'skua.swf'
}
else {
    $outputPath = [System.IO.Path]::GetFullPath($OutputPath)
}

if (-not (Test-Path -LiteralPath $mainSourcePath)) {
    throw "AS3 entry point was not found: $mainSourcePath"
}

if ([string]::IsNullOrWhiteSpace($MxmlcPath)) {
    $mxmlcCommand = Get-Command mxmlc -ErrorAction SilentlyContinue
    if ($mxmlcCommand) {
        $MxmlcPath = $mxmlcCommand.Source
    }
}

if ([string]::IsNullOrWhiteSpace($MxmlcPath) -and $env:FLEX_HOME) {
    $compilerNames = if ($IsWindows) { @('mxmlc.bat', 'mxmlc.cmd', 'mxmlc') } else { @('mxmlc') }
    foreach ($compilerName in $compilerNames) {
        $candidate = Join-Path $env:FLEX_HOME "bin/$compilerName"
        if (Test-Path -LiteralPath $candidate) {
            $MxmlcPath = $candidate
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($MxmlcPath)) {
    if ($IsWindows) {
        $searchRoots = @(
            'C:\MoonshineSDKs',
            'C:\ApacheFlex',
            $env:ProgramFiles,
            ${env:ProgramFiles(x86)},
            $env:LOCALAPPDATA
        )
        $compilerNames = @('mxmlc.bat', 'mxmlc.cmd', 'mxmlc')
    }
    else {
        $searchRoots = @(
            $env:FLEX_HOME,
            (Join-Path $HOME '.flex-sdk'),
            (Join-Path $HOME 'ApacheFlex'),
            '/opt/apache-flex',
            '/opt/flex-sdk',
            '/usr/local/apache-flex',
            '/opt/homebrew/opt/apache-flex'
        )
        $compilerNames = @('mxmlc')
    }

    $searchRoots = $searchRoots | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_)
    } | Select-Object -Unique

    foreach ($searchRoot in $searchRoots) {
        foreach ($compilerName in $compilerNames) {
            $foundCompiler = Get-ChildItem -LiteralPath $searchRoot -Filter $compilerName -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($foundCompiler) {
                $MxmlcPath = $foundCompiler.FullName
                break
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($MxmlcPath)) {
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($MxmlcPath)) {
    throw 'Flex mxmlc was not found. Add mxmlc to PATH, set FLEX_HOME, or pass -MxmlcPath.'
}

$resolvedCommand = Get-Command -Name $MxmlcPath -ErrorAction SilentlyContinue
if ($resolvedCommand) {
    $MxmlcPath = $resolvedCommand.Source
}

if (-not (Test-Path -LiteralPath $MxmlcPath)) {
    throw "Flex mxmlc was not found: $MxmlcPath"
}

if ([string]::IsNullOrWhiteSpace($env:FLEX_HOME)) {
    $mxmlcDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $MxmlcPath).Path
    $env:FLEX_HOME = Split-Path -Parent $mxmlcDirectory
}
if ([string]::IsNullOrWhiteSpace($env:PLAYERGLOBAL_HOME)) {
    $env:PLAYERGLOBAL_HOME = Join-Path $env:FLEX_HOME 'frameworks/libs/player'
}

$resolvedOutputDirectory = Split-Path -Parent $outputPath
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

& $MxmlcPath `
    -source-path $sourcePath `
    -default-size 958 550 `
    -output $outputPath `
    $mainSourcePath `
    -target-player 28.0 `
    -optimize

if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputPath)) {
    throw "AS3 compilation failed: $outputPath"
}

$output = Get-Item -LiteralPath $outputPath
Write-Host "AS3 game content built: $($output.FullName) ($($output.Length) bytes)" -ForegroundColor Green
exit 0
