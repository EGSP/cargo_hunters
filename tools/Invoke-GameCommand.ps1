param(
    [string]$Route = '/health',
    [ValidateSet('GET', 'POST')][string]$Method = 'GET',
    [string]$Body,
    [string]$GameDirectory = 'C:\workroot\software\steam\steamapps\common\Cargo Hunters'
)

$ErrorActionPreference = 'Stop'
$connectionPath = Join-Path $GameDirectory 'BepInEx\config\cargo-dev-bridge.json'
$connection = Get-Content -LiteralPath $connectionPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $Route.StartsWith('/') -or $Route.StartsWith('//')) { throw 'Route must start with one slash' }
$parameters = @{
    Uri = $connection.url + $Route
    Method = $Method
    Headers = @{ Authorization = 'Bearer ' + $connection.token }
    TimeoutSec = 20
}
if ($Body) {
    $parameters.Body = [System.Text.Encoding]::UTF8.GetBytes($Body)
    $parameters.ContentType = 'application/json; charset=utf-8'
}
Invoke-RestMethod @parameters | ConvertTo-Json -Depth 100
