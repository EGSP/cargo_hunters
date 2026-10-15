param([string]$GameDirectory = 'C:\workroot\software\steam\steamapps\common\Cargo Hunters')

$ErrorActionPreference = 'Stop'
if (Get-Process CargoHunters -ErrorAction SilentlyContinue) { throw 'Close Cargo Hunters before deploying the plugin.' }
$projectPath = Join-Path $PSScriptRoot '..\src\CargoHunters.InventoryMod\CargoHunters.InventoryMod.csproj'
dotnet build $projectPath -c Release --nologo "-p:GameDirectory=$GameDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$outputPath = Join-Path $PSScriptRoot '..\src\CargoHunters.InventoryMod\bin\Release\net6.0\CargoHunters.InventoryMod.dll'
$destinationPath = Join-Path $GameDirectory 'BepInEx\plugins\CargoHunters.InventoryMod'
New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
Copy-Item -LiteralPath $outputPath -Destination $destinationPath
