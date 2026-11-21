# Keep this fork current with upstream without losing fork-specific work.
#
#   .\scripts\sync-upstream.ps1            fetch, then merge upstream/main into the current branch
#   .\scripts\sync-upstream.ps1 -CheckOnly report what is new and stop
#   .\scripts\sync-upstream.ps1 -Push      also push the merge to the fork
#
# The fork's commits are never rewritten: this merges, so the next sync is the same command.
# A conflict stops the script with git's own message; resolve, then "git commit".
param([switch]$CheckOnly, [switch]$Push)

$ErrorActionPreference = 'Stop'
$branch = (git rev-parse --abbrev-ref HEAD)
if ($branch -ne 'dev/kiosk-persistence') { Write-Warning "on '$branch', not dev/kiosk-persistence" }

git fetch --tags upstream
$behind = [int](git rev-list --count "HEAD..upstream/main")
if ($behind -eq 0) { Write-Host "Up to date with upstream ($((git describe --tags --always upstream/main)))."; return }

Write-Host "$behind upstream commit(s) to take:"
git log --oneline "HEAD..upstream/main"
if ($CheckOnly) { Write-Host "Check only; nothing merged."; return }

git merge upstream/main --no-edit
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Merge stopped with conflicts. Resolve them, then: git commit" -ForegroundColor Yellow
    Write-Host "Files in conflict:" -ForegroundColor Yellow
    git diff --name-only --diff-filter=U
    exit 1
}

Write-Host "Merged: $(git log --oneline -1)"
if ($Push) { git push origin $branch }
Write-Host ""
Write-Host "Next: rebuild both artifacts and deploy (see the runbook in IR_REMOTE_REPORT.md)."
Write-Host "The fork APK needs -PphoneboxVersionCode above the installed one."
