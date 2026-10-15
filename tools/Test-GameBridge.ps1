param([string]$GameDirectory = 'C:\workroot\software\steam\steamapps\common\Cargo Hunters')

$ErrorActionPreference = 'Stop'
$commandPath = Join-Path $PSScriptRoot 'Invoke-GameCommand.ps1'
function Request([string]$Route, [string]$Method = 'GET', [string]$Body = '') {
    & $commandPath -Route $Route -Method $Method -Body $Body -GameDirectory $GameDirectory | ConvertFrom-Json
}
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

$health = Request '/health'
Assert ($health.ready -eq $true) 'Bridge is not ready'
$scenes = Request '/scenes'
Assert ($scenes.scenes.Count -ge 1) 'No loaded scenes returned'
$roots = Request '/objects?limit=100&depth=0'
Assert ($roots.objects.Count -gt 0) 'No scene roots returned'
$sample = $roots.objects | Where-Object name -eq 'SoundSources' | Select-Object -First 1
if (-not $sample) { $sample = $roots.objects | Select-Object -First 1 }
$detail = Request ('/object?id=' + $sample.id)
$transform = $detail.components | Where-Object type -eq 'UnityEngine.Transform' | Select-Object -First 1
Assert ($null -ne $transform) 'Transform component missing'
$read = Request ('/read?id=' + $transform.id + '&member=childCount')
Assert ($read.value -ge 0) 'Failed to read Transform.childCount'
$position = Request ('/read?id=' + $transform.id + '&member=position')
Assert ($null -ne $position.value.x) 'Vector3 position was not serialized'
$inspection = Request ('/component?id=' + $transform.id)
Assert ($inspection.methods.Count -gt 0) 'Component methods missing'
$invoke = Request '/invoke' 'POST' (@{ id = $sample.id; method = 'GetInstanceID'; arguments = @() } | ConvertTo-Json -Compress)
Assert ($invoke.value -eq $sample.id) 'Native method returned unexpected object ID'
$subtree = Request ('/objects?rootId=' + $sample.id + '&depth=0&limit=1')
Assert ($subtree.objects.Count -eq 1 -and $subtree.objects[0].id -eq $sample.id) 'Subtree filtering failed'
$script = Request '/scripts/run' 'POST' '{"name":"scene-summary"}'
Assert ($script.script -eq 'scene-summary') 'Named script failed'

$connection = Get-Content -LiteralPath (Join-Path $GameDirectory 'BepInEx\config\cargo-dev-bridge.json') -Raw | ConvertFrom-Json
try {
    Invoke-WebRequest -Uri ($connection.url + '/health') -UseBasicParsing -TimeoutSec 10 | Out-Null
    throw 'Unauthenticated request unexpectedly succeeded'
} catch {
    Assert ([int]$_.Exception.Response.StatusCode -eq 401) 'Expected HTTP 401 without token'
}
try {
    Request '/object?id=2147483647' | Out-Null
    throw 'Invalid ID unexpectedly succeeded'
} catch {
    Assert ([int]$_.Exception.Response.StatusCode -eq 400) 'Expected HTTP 400 for invalid ID'
}

[pscustomobject]@{
    passed = $true
    processId = $health.processId
    scene = $scenes.active
    checked = @('health', 'scenes', 'objects', 'object', 'component', 'read', 'Vector3', 'invoke', 'rootId', 'scripts', 'unauthorized 401', 'invalid ID 400')
} | ConvertTo-Json -Depth 4
