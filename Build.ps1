<#
.SYNOPSIS
Builds HomefrontModLoader (msimg32.dll), HomefrontSDK (generated bindings, HomefrontSDK.dll) and the mods (HelloMod,
HomefrontBots); optionally installs them into the dedicated server.

.DESCRIPTION
  HomefrontSDK\Build.ps1             build everything
  HomefrontSDK\Build.ps1 -Install    also install into Homefront Dedicated Server\Binaries (stops nothing: the server must not be running)
  HomefrontSDK\Build.ps1 -Generate   regenerate the bindings even if api-reference.json hasn't changed

The bindings (HomefrontSDK\SDK\Generated) are generated from HomefrontSDK\api-reference.json when they are
missing or older than it. Refresh that file from a running server with: python HomefrontSDK\Tools\dump_sdk.py --api

Installed layout (Homefront Dedicated Server\Binaries):
  msimg32.dll                                   HomefrontModLoader: VAC off, bootstrap, hand-off to the SDK
  HomefrontSDK\HomefrontSDK.dll (+ .runtimeconfig.json, .deps.json, .pdb)
  HomefrontSDK\Mods\<Mod>\<Mod>.dll (+ .pdb)    each mod (HelloMod, HomefrontBots)
#>
param(
    [switch]$Install,
    [switch]$Generate,
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$sdk = $PSScriptRoot                # this folder, HomefrontSDK
$root = Split-Path -Parent $sdk     # the project: HomefrontBots and "Homefront Dedicated Server" beside it
$api = Join-Path $sdk 'api-reference.json'
$generated = Join-Path $sdk 'SDK\Generated'
$bin = Join-Path $root 'Homefront Dedicated Server\Binaries'

function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }
function Run($exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed ($LASTEXITCODE)" }
}

Step 'HomefrontModLoader'
Run 'cmd.exe' @('/c', (Join-Path $root 'HomefrontModLoader\build.cmd'))

$registry = Join-Path $generated 'Registry.cs'
if ($Generate -or -not (Test-Path $registry) -or (Get-Item $api).LastWriteTime -gt (Get-Item $registry).LastWriteTime) {
    Step 'bindings'
    Run 'dotnet' @('run', '--project', (Join-Path $sdk 'Generator'), '-c', $Configuration, '--', $api, $generated)
}

Step 'managed projects'
Run 'dotnet' @('build', (Join-Path $sdk 'Homefront.slnx'), '-c', $Configuration, '-v', 'minimal', '-nologo')

if (-not $Install) { return }

Step "install into $bin"
if (Get-Process HFDedicated -ErrorAction SilentlyContinue) { throw 'the server is running; stop it first' }
$sdkOut = Join-Path $sdk "SDK\bin\$Configuration\net10.0"
$sdkDir = Join-Path $bin 'HomefrontSDK'
$modsDir = Join-Path $sdkDir 'Mods'
New-Item -ItemType Directory -Force $modsDir | Out-Null
Copy-Item (Join-Path $root 'HomefrontModLoader\build\msimg32.dll') $bin -Force
foreach ($f in 'HomefrontSDK.dll', 'HomefrontSDK.pdb', 'HomefrontSDK.runtimeconfig.json', 'HomefrontSDK.deps.json') {
    Copy-Item (Join-Path $sdkOut $f) $sdkDir -Force
}
$mods = @(
    @{ Name = 'HelloMod'; Out = Join-Path $sdk "Examples\HelloMod\bin\$Configuration\net10.0" },
    @{ Name = 'HomefrontBots'; Out = Join-Path $root "HomefrontBots\bin\$Configuration\net10.0" }
)
foreach ($m in $mods) {
    if (-not (Test-Path (Join-Path $m.Out "$($m.Name).dll"))) { continue }
    $dest = Join-Path $modsDir $m.Name
    New-Item -ItemType Directory -Force $dest | Out-Null
    foreach ($ext in 'dll', 'pdb') {
        $f = Join-Path $m.Out "$($m.Name).$ext"
        if (Test-Path $f) { Copy-Item $f $dest -Force }
    }
    Write-Host "  mod $($m.Name)"
}
Write-Host "installed; logs: $sdkDir\HomefrontSDK.log and Mods\<Mod>\<Mod>.log"
