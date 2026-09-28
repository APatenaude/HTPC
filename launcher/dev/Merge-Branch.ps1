#Requires -Version 5.1
<#
.SYNOPSIS
    Dev: merges a helper's branch the careful way: in a scratch worktree, never in the main
    checkout, then fast-forwards the main branch only when the merge is clean (and, with -Test,
    green).

.DESCRIPTION
    The lead's routine while several helpers (Claude subagents in their own git worktrees) work in
    parallel. Merges caused regressions early on, so:
    1. A scratch worktree (.claude\worktrees\merge-scratch, detached) at the main branch's tip.
    2. git merge --no-ff <branch or sha> there, with the message from a file (no BOM).
    3. Conflicts: the script stops and lists them, and the worktree stays for resolving by hand.
       Resolve keeping BOTH sides' behaviour; read each hunk, never take one side blindly. When
       two helpers restructured the same function, hand the merge to the helper that knows it.
       Then: git -C <scratch> add ...; git -C <scratch> commit --no-edit; run this again with
       -Continue.
    4. Optional -Test: Test-All.ps1 on the scratch tree.
    5. Fast-forward the main branch to the scratch tip. Nothing is pushed.
.PARAMETER Ref
    The branch or commit to merge.
.PARAMETER Message
    The merge commit's subject (a Co-Authored-By line is not added: add it in the text if needed).
.PARAMETER Continue
    After resolving conflicts in the scratch worktree: test (with -Test) and fast-forward.
.PARAMETER Test
    Run Test-All.ps1 on the merged tree before fast-forwarding; stop if it fails.
.EXAMPLE
    .\Merge-Branch.ps1 -Ref worktree-agent-abc123 -Message 'Merge the TV UI fixes' -Test
#>
param(
    [string]$Ref,
    [string]$Message,
    [switch]$Continue,
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
# git reports progress on stderr; with Stop, PowerShell 5.1 would turn that into an error.
function Invoke-Git { $ErrorActionPreference = 'Continue'; & git @args 2>&1 | ForEach-Object { "$_" } }
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$scratch = Join-Path $repo '.claude\worktrees\merge-scratch'
$branch = (git -C $repo rev-parse --abbrev-ref HEAD).Trim()

if (-not $Continue) {
    if (-not $Ref -or -not $Message) { throw 'Give -Ref and -Message (or -Continue after resolving)' }
    if (git -C $repo status --porcelain) { throw 'The main checkout has changes: commit or set them aside first' }
    if (Test-Path $scratch) { Invoke-Git -C $repo worktree remove --force $scratch | Out-Null }
    Invoke-Git -C $repo worktree add --detach $scratch $branch | Out-Null
    $msgFile = Join-Path $env:TEMP "merge-branch-$PID.txt"
    [IO.File]::WriteAllText($msgFile, "$Message`n", (New-Object Text.UTF8Encoding $false))
    Invoke-Git -C $scratch merge --no-ff -F $msgFile $Ref | Select-String 'CONFLICT|changed|Already'
    Remove-Item $msgFile -ErrorAction SilentlyContinue
    $conflicts = @(git -C $scratch diff --name-only --diff-filter=U)
    if ($conflicts.Count) {
        "CONFLICTS in $scratch (resolve keeping both sides, commit, then -Continue):"
        $conflicts | ForEach-Object { "  $_" }
        exit 2
    }
}
if (@(git -C $scratch diff --name-only --diff-filter=U).Count) { throw 'Conflicts left in the scratch worktree' }
if ($Test) {
    & (Join-Path $PSScriptRoot 'Test-All.ps1') -Root $scratch
    if ($LASTEXITCODE -ne 0) { "Tests failed: nothing fast-forwarded. Fix in $scratch, commit, -Continue -Test."; exit 1 }
}
$tip = (git -C $scratch rev-parse HEAD).Trim()
Invoke-Git -C $repo merge --ff-only $tip | Select-Object -Last 1
git -C $repo log --oneline -1
