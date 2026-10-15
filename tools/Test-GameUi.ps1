$ErrorActionPreference = 'Stop'
$commandPath = Join-Path $PSScriptRoot 'Invoke-GameCommand.ps1'
$tree = & $commandPath -Route '/ui/tree?depth=30&limit=2000&includeInactive=true' | ConvertFrom-Json
if ($tree.roots.Count -eq 0 -or $tree.count -eq 0) { throw 'No UI roots returned' }
$nodeCount = 0
$controls = 0
function Check-Nodes($Nodes) {
    foreach ($node in $Nodes) {
        if ($node -is [string] -or $null -eq $node.id -or $null -eq $node.kind) { throw 'UI tree was truncated by JSON serialization' }
        $script:nodeCount++
        if ($node.kind -in @('Button', 'Toggle', 'InputField', 'Dropdown', 'Slider')) { $script:controls++ }
        Check-Nodes $node.children
    }
}
Check-Nodes $tree.roots
if ($nodeCount -ne $tree.count) { throw 'Reported node count differs from actual tree' }
$limited = & $commandPath -Route ('/ui/tree?rootId=' + $tree.roots[0].id + '&limit=1&depth=30&includeInactive=true') | ConvertFrom-Json
if ($limited.count -ne 1 -or $limited.roots[0].id -ne $tree.roots[0].id) { throw 'UI subtree or limit failed' }
try {
    & $commandPath -Route '/ui/click' -Method POST -Body '{"id":2147483647}' | Out-Null
    throw 'Click unexpectedly accepted an unknown ID'
} catch {
    if ([int]$_.Exception.Response.StatusCode -ne 400) { throw }
}
[pscustomobject]@{ passed = $true; nodes = $nodeCount; controls = $controls; truncated = $tree.truncated; checked = @('deep UI JSON', 'node IDs', 'node count', 'subtree', 'limit', 'invalid click 400') } | ConvertTo-Json -Depth 4
