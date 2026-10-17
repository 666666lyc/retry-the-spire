[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$GameDir = $env:STS2_GAME_DIR,
    [switch]$Install
)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) {
    $GameDir = Join-Path ${env:ProgramFiles(x86)} 'Steam/steamapps/common/Slay the Spire 2'
}
$GameDir = [System.IO.Path]::GetFullPath($GameDir)
$gameData = Join-Path $GameDir 'data_sts2_windows_x86_64'
foreach ($dll in @('sts2.dll', 'GodotSharp.dll', '0Harmony.dll', 'Steamworks.NET.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $gameData $dll) -PathType Leaf)) {
        throw "Missing game assembly: $gameData/$dll. Pass -GameDir or set STS2_GAME_DIR."
    }
}
Get-Command dotnet -ErrorAction Stop | Out-Null
$outputDir = Join-Path $PSScriptRoot 'out/RetryTheSpire'
Push-Location $PSScriptRoot
try {
    & dotnet build RetryTheSpire.csproj -c $Configuration -o $outputDir "-p:STS2GameDir=$GameDir"
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
    if ($Install) {
        $installDir = Join-Path $GameDir 'mods/RetryTheSpire'
        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $outputDir 'RetryTheSpire.dll') -Destination $installDir
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'mod_manifest.json') -Destination $installDir
        Write-Host "Installed to $installDir"
    }
}
finally { Pop-Location }
