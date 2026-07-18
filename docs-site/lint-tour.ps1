# W5-P1 guided-tour lint (PowerShell twin of lint-tour.sh — same rules, for local use on
# Windows). See lint-tour.sh for the rule set; docs-CI runs the sh version.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot 'docs')

$members = @('start/getting-started.md', 'start/concepts.md') +
    (Get-ChildItem guides -Filter *.md | Sort-Object Name | ForEach-Object { "guides/$($_.Name)" })

function Fail([string]$message) { Write-Error "lint-tour: $message"; exit 1 }

$tourNext = @{}; $tourPrev = @{}
foreach ($f in $members) {
    if (-not (Test-Path $f)) { Fail "tour member does not exist: $f" }
    $lines = Get-Content $f
    if ($lines[0] -notmatch '^---\s*$') { Fail "$f has no front matter block" }
    $fm = @()
    for ($i = 1; $i -lt $lines.Count -and $lines[$i] -notmatch '^---\s*$'; $i++) { $fm += $lines[$i] }
    $next = ($fm | Where-Object { $_ -match '^tour_next:\s*(.*)$' } | ForEach-Object { $Matches[1].Trim() }) | Select-Object -First 1
    $prev = ($fm | Where-Object { $_ -match '^tour_prev:\s*(.*)$' } | ForEach-Object { $Matches[1].Trim() }) | Select-Object -First 1
    if (-not $next -and -not $prev) { Fail "$f has no tour_prev/tour_next front matter" }
    $tourNext[$f] = "$next"; $tourPrev[$f] = "$prev"
}

$start = $members | Where-Object { -not $tourPrev[$_] }
$end = $members | Where-Object { -not $tourNext[$_] }
if (@($start).Count -ne 1) { Fail "expected exactly one chain start, found: $start" }
if (@($end).Count -ne 1) { Fail "expected exactly one chain end, found: $end" }

$walked = @{}; $current = @($start)[0]; $prev = ''
while ($current) {
    if ($members -notcontains $current) { Fail "chain leaves the tour set at: $current" }
    if ($walked.ContainsKey($current)) { Fail "cycle detected: $current visited twice" }
    if ($tourPrev[$current] -ne $prev) { Fail "${current}: tour_prev is '$($tourPrev[$current])' but the chain arrives from '$prev'" }
    $walked[$current] = $true
    $prev = $current
    $current = $tourNext[$current]
}

if ($walked.Count -ne $members.Count) {
    $orphans = $members | Where-Object { -not $walked.ContainsKey($_) }
    Fail "chain visits $($walked.Count) of $($members.Count) pages — orphaned: $($orphans -join ', ')"
}

Write-Host "lint-tour: chain OK — $($walked.Count) pages, $(@($start)[0]) → $(@($end)[0])"
