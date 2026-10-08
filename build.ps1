# Builds tribe with the C# compiler bundled with Windows (.NET Framework 4.x, C# 5).
#
#   core\ -> the patch that runs inside Green Hell, compiled against the game's own assemblies
#   app\  -> bin\tribe.exe, one file with the core embedded: this is what players get
#
# Every build gives the core a unique assembly name, so a game that still has an older core loaded takes the new one.
# A tribe.exe started from bin\ runs from a copy, so this never hits a locked file, and that running copy restarts
# itself onto the new build. Game files are only read (references), never changed.
param([string]$Game = "C:\Program Files (x86)\Steam\steamapps\common\Green Hell")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$managed = Join-Path $Game "GH_Data\Managed"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$bin = Join-Path $root "bin"
$obj = Join-Path $bin "obj"
New-Item -ItemType Directory -Force $obj | Out-Null
if (-not (Test-Path (Join-Path $managed "Assembly-CSharp.dll"))) { throw "Green Hell not found at $Game (pass -Game)" }

# csc fails with CS1619 when TEMP is a very long path; give it a short one.
$tmp = Join-Path $env:LOCALAPPDATA "TribeBuild"
New-Item -ItemType Directory -Force $tmp | Out-Null
$env:TMP = $tmp; $env:TEMP = $tmp

function Sources([string]$dir) { Get-ChildItem (Join-Path $root $dir) -Filter *.cs | Sort-Object Name | ForEach-Object { "`"$($_.FullName)`"" } }

Get-ChildItem $obj -Filter "Tribe.Core_*.dll" -ErrorAction SilentlyContinue | Remove-Item -Force
$core = Join-Path $obj ("Tribe.Core_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".dll")
$coreName = [IO.Path]::GetFileNameWithoutExtension($core)
$coreNameFile = Join-Path $obj "Tribe.CoreName.txt"
[IO.File]::WriteAllText($coreNameFile, $coreName, [Text.Encoding]::ASCII)
$refs = Get-ChildItem $managed -Filter *.dll | ForEach-Object { "/r:`"$($_.FullName)`"" }
& $csc /nologo /target:library /optimize+ /nostdlib+ /noconfig /nowarn:1701,1702 "/out:$core" @refs @(Sources "core")
if ($LASTEXITCODE -ne 0) { throw "Build failed (core)" }

$icon = Join-Path $root "assets\tribe.ico"
if (-not (Test-Path $icon)) { & (Join-Path $root "assets\make-icon.ps1") }
$exe = Join-Path $bin "tribe.exe"
& $csc /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$icon" "/win32manifest:$(Join-Path $root 'app\tribe.manifest')" "/resource:$core,tribe.core.dll" "/resource:$coreNameFile,tribe.core.name" "/out:$exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll @(Sources "app")
if ($LASTEXITCODE -ne 0) { throw "Build failed (app)" }
Write-Host "built $exe ($([IO.Path]::GetFileNameWithoutExtension($core)) embedded)"
