param(
    [int]$Depth = 20,
    [int]$Limit = 2500,
    [int]$RootId = 0,
    [switch]$IncludeInactive,
    [switch]$FullTree
)

$ErrorActionPreference = 'Stop'
$route = "/ui/tree?depth=$Depth&limit=$Limit&rootId=$RootId&includeInactive=" + $IncludeInactive.IsPresent.ToString().ToLowerInvariant()
$json = & (Join-Path $PSScriptRoot 'Invoke-GameCommand.ps1') -Route $route
if ($FullTree) { $json; return }
$tree = $json | ConvertFrom-Json
function Flatten($Nodes) {
    foreach ($node in $Nodes) {
        if ($node.kind -in @('Button', 'Toggle', 'InputField', 'Dropdown', 'Slider') -or $node.text) {
            [pscustomobject]@{
                id = $node.id; kind = $node.kind; name = $node.name; text = $node.text
                visible = $node.visibleEstimate; interactable = $node.interactable; path = $node.path
            }
        }
        Flatten $node.children
    }
}
[pscustomobject]@{ screen = $tree.screen; count = $tree.count; truncated = $tree.truncated; elements = @(Flatten $tree.roots) } | ConvertTo-Json -Depth 6
