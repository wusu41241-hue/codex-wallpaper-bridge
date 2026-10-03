[CmdletBinding()]
param(
    [string]$NodePath,
    [string]$ScratchRoot=[System.IO.Path]::GetTempPath()
)

$ErrorActionPreference='Stop'
$repositoryRoot=Split-Path -Parent $PSScriptRoot
if(-not $NodePath){
    $nodeCommand=Get-Command node.exe -ErrorAction SilentlyContinue
    if(-not $nodeCommand){$nodeCommand=Get-Command node -ErrorAction SilentlyContinue}
    if(-not $nodeCommand){throw 'Node.js 22 or newer is required. Pass -NodePath or put Node.js in PATH.'}
    $NodePath=$nodeCommand.Source
}
$NodePath=(Resolve-Path -LiteralPath $NodePath).ProviderPath
$nodeVersion=(& $NodePath -p 'process.versions.node' | Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^([0-9]+)\.' -or [int]$Matches[1] -lt 22){
    throw "Node.js 22 or newer is required; found '$nodeVersion'."
}

# These tests compile the production C# sources into an isolated test executable.
# GUI actions use a fake service and cannot start or change a running Codex app.
& (Join-Path $repositoryRoot 'automation\tests\run-controller-behavior-tests.ps1') -ScratchRoot $ScratchRoot

$injector=Join-Path $repositoryRoot 'windows\scripts\injector.mjs'
foreach($argument in @('--self-test','--check-payload')){
    & $NodePath $injector $argument
    if($LASTEXITCODE -ne 0){throw "Injector smoke check $argument failed (exit $LASTEXITCODE)."}
}
$nodeTests=@(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'windows\tests') -Filter '*.test.mjs' -File | Sort-Object Name)
if($nodeTests.Count -eq 0){throw 'No Node.js regression tests were found.'}
foreach($test in $nodeTests){
    & $NodePath $test.FullName
    if($LASTEXITCODE -ne 0){throw "$($test.Name) failed (exit $LASTEXITCODE)."}
}
Write-Host "PASS: portable controller behavior tests, injector payload checks, and $($nodeTests.Count) Node.js regression suites."
