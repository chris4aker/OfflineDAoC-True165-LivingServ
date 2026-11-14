# Living Server for Offline DAoC True 1.65: undoes the newest install (ROLLBACK.cmd runs it). Each run undoes one install,
# newest first, so running it again after an upgrade goes back one build further, down to the released game.
# It never puts the world database back: that would undo the play since (the backup is there to restore by hand).
# Windows PowerShell 5.1 compatible.
param(
    [string]$GameFolder = '',
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
$buildDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $buildDir 'fork-build-common.ps1')

$game = Find-GameFolder $GameFolder $buildDir
Write-Host "Game folder: $game"
Assert-GameClosed
$records = @(Get-InstallRecords $game)
if ($records.Count -eq 0) { Stop-WithMessage 'there is no Living Server install to undo in this game folder.' }
$record = $records[-1]

# Every file must still be the one that install put there: a file changed since is not overwritten.
foreach ($f in $record.Files) {
    $path = Join-Game $game $f.Game
    $now = if (Test-Path -LiteralPath $path) { Get-Sha256 $path } else { $null }
    if ($now -ne $f.Installed) { Stop-WithMessage "$($f.Game) changed after Living Server $($record.Build) was installed, so it isn't put back." }
    if ($f.Backup -and !(Test-Path -LiteralPath (Join-Game $record.Folder $f.Backup))) { Stop-WithMessage "the backup of $($f.Game) is missing in $($record.Folder)." }
}
foreach ($f in $record.Files) {
    $path = Join-Game $game $f.Game
    if ($f.Backup) {
        Copy-Item -LiteralPath (Join-Game $record.Folder $f.Backup) -Destination $path -Force
        if ((Get-Sha256 $path) -ne $f.Before) { Stop-WithMessage "$($f.Game) did not come back as it was. Copy it by hand from $($record.Folder)." }
    } else {
        Remove-Item -LiteralPath $path -Force
    }
}
Rename-Item -LiteralPath (Join-Path $record.Folder 'install-record.json') -NewName 'install-record.rolled-back.json'
Write-Host ''
Write-Host "Living Server $($record.Build) was undone: $(@($record.Files).Count) files put back and checked." -ForegroundColor Green
if ($records.Count -gt 1) { Write-Host "Run ROLLBACK.cmd again to undo $($records[-2].Build) too." }
if ($record.Database) { Write-Host "The world database is as you left it. Its copy from before that install: $($record.Folder)\data" }
if (!$NoPause) { Read-Host 'Press Enter to close' | Out-Null }
exit 0
