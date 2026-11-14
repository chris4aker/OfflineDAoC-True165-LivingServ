# Living Server for Offline DAoC True 1.65: installs this package into a True 1.65 game folder. INSTALL.cmd runs it.
#
# - checks the game is a release this build fits (VERSION.txt against BUILD-INFO.txt's BaseRelease);
# - refuses while the server, the launcher or the game runs;
# - checks every file this package ships against BUILD-INFO.txt, and every file it replaces: each must be the released
#   one (BUILD-INFO.txt "base/..."), an earlier Living Server build's (its install record), or already this build's.
#   A file changed by something else is refused, unless -Force (it is backed up like the rest);
# - backs up the world database (runtime\data\opendaoc-165.sqlite3.db and its -wal and -shm files) and every file it
#   replaces into runtime\server\deployment-backups\livingserver-<build>-<time>, and checks each copy;
# - copies, checks every copy, and on any failure puts back what it had already replaced;
# - writes install-record.json beside the backups, which ROLLBACK.cmd reads.
# Windows PowerShell 5.1 compatible.
param(
    [string]$GameFolder = '',
    [switch]$Force,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
$buildDir = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $buildDir 'fork-build-common.ps1')

$info = Read-KeyValueFile (Join-Path $buildDir 'BUILD-INFO.txt')
$build = $info['BuildName']
if (!$build) { Stop-WithMessage 'BUILD-INFO.txt names no build: this package is damaged. Download it again.' }
Write-Host "Living Server $build for Offline DAoC True 1.65"
Write-Host ''

# 1. The package itself.
foreach ($key in @($info.Keys)) {
    if ($key -notmatch '^(server|launcher|docs)/') { continue }
    $file = Join-Game $buildDir $key
    if (!(Test-Path -LiteralPath $file)) { Stop-WithMessage "the package is missing $key. Download it again." }
    if ((Get-Sha256 $file) -ne $info[$key].ToLowerInvariant()) { Stop-WithMessage "$key in the package is not the file BUILD-INFO.txt names. Download it again." }
}

# 2. The game.
$game = Find-GameFolder $GameFolder $buildDir
Write-Host "Game folder: $game"
$release = Get-GameRelease $game
$fits = @(($info['BaseRelease'] -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($fits -notcontains $release) {
    $shown = if ($release) { "v$release" } else { 'an unknown version (no VERSION.txt)' }
    if (!$Force) { Stop-WithMessage "this game is Offline DAoC True 1.65 $shown. This build fits v$($fits -join ', v') only." }
    Write-Host "-Force: installing on $shown although this build fits v$($fits -join ', v')." -ForegroundColor Yellow
}
Assert-GameClosed

# 3. What will be replaced, and whether each file is one this package may replace.
$records = @(Get-InstallRecords $game)
$known = @{}
foreach ($record in $records) { foreach ($f in $record.Files) { if ($f.Installed) { $known["$($f.Game)|$($f.Installed)"] = $record.Build } } }
$plan = @()
$changed = @()
foreach ($target in (Get-Targets $game $buildDir $info)) {
    $path = Join-Game $game $target.Game
    $exists = Test-Path -LiteralPath $path
    $before = if ($exists) { Get-Sha256 $path } else { $null }
    if ($before -eq $target.Sha.ToLowerInvariant()) { continue }   # already this build's
    $ok = !$exists -and !$target.Base -or ($before -and ($before -eq $target.Base -or $known.ContainsKey("$($target.Game)|$before")))
    if (!$ok) { $changed += $target.Game }
    $plan += [pscustomobject]@{ Target = $target; Path = $path; Exists = $exists; Before = $before }
}
if ($plan.Count -eq 0) {
    Write-Host "Living Server $build is already installed: every file is this build's. Nothing to do." -ForegroundColor Green
    if (!$NoPause) { Read-Host 'Press Enter to close' | Out-Null }
    exit 0
}
if ($changed.Count -gt 0) {
    $list = ($changed | Select-Object -First 8) -join ', '
    if (!$Force) {
        Stop-WithMessage ("these files are neither the released True 1.65 ones nor an earlier Living Server build's, so something else " +
            "changed them: $list. Run INSTALL.cmd -Force to replace them anyway (they are backed up first).")
    }
    Write-Host "-Force: replacing files something else changed (backed up first): $list" -ForegroundColor Yellow
}

# 4. Backups: the world database first, then every file to be replaced.
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Game $game "$($Script:BackupRoot)/livingserver-$build-$stamp"
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$database = Join-Game $game "runtime/data/$($Script:DatabaseName)"
$databaseSaved = $false
if (Test-Path -LiteralPath $database) {
    $dataBackup = Join-Path $backup 'data'
    New-Item -ItemType Directory -Path $dataBackup -Force | Out-Null
    foreach ($suffix in @('', '-wal', '-shm')) {
        $from = "$database$suffix"
        if (!(Test-Path -LiteralPath $from)) { continue }
        $to = Join-Path $dataBackup ((Split-Path -Leaf $database) + $suffix)
        Copy-Item -LiteralPath $from -Destination $to
        if ((Get-Sha256 $from) -ne (Get-Sha256 $to)) { Stop-WithMessage "the copy of $from in $dataBackup does not match. Nothing was replaced." }
    }
    $databaseSaved = $true
    Write-Host "World database backed up: $dataBackup"
}
$files = @()
foreach ($item in $plan) {
    $saved = $null
    if ($item.Exists) {
        $saved = "files/$($item.Target.Game)"
        $to = Join-Game $backup $saved
        New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
        Copy-Item -LiteralPath $item.Path -Destination $to
        if ((Get-Sha256 $to) -ne $item.Before) { Stop-WithMessage "the backup of $($item.Target.Game) does not match. Nothing was replaced." }
    }
    $files += [pscustomobject]@{ Game = $item.Target.Game; Before = $item.Before; Installed = $item.Target.Sha.ToLowerInvariant(); Backup = $saved }
}

# 5. Copy and check; on a failure put back what was already replaced.
$done = @()
try {
    foreach ($item in $plan) {
        Copy-Item -LiteralPath (Join-Game $buildDir $item.Target.Package) -Destination $item.Path -Force
        $done += $item
        if ((Get-Sha256 $item.Path) -ne $item.Target.Sha.ToLowerInvariant()) { throw "the copy of $($item.Target.Game) does not match the package" }
    }
} catch {
    foreach ($item in $done) {
        if ($item.Exists) { Copy-Item -LiteralPath (Join-Game $backup "files/$($item.Target.Game)") -Destination $item.Path -Force }
        else { Remove-Item -LiteralPath $item.Path -Force -ErrorAction SilentlyContinue }
    }
    Stop-WithMessage "$($_.Exception.Message). Every file was put back as it was."
}

# 6. The record ROLLBACK.cmd reads, the docs, and only the newest three database copies.
$record = [pscustomobject]@{
    Build = $build; GameRelease = $release; Installed = (Get-Date).ToUniversalTime().ToString('o'); Forced = [bool]$Force
    Database = $databaseSaved; Files = $files
}
$record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'install-record.json') -Encoding UTF8
$docs = Join-Path $buildDir 'docs'
if (Test-Path -LiteralPath $docs) {
    $target = Join-Game $game "docs/living-server-$build"
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -Path (Join-Path $docs '*') -Destination $target -Recurse -Force
}
$withData = @(Get-ChildItem -LiteralPath (Join-Game $game $Script:BackupRoot) -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'data') } | Sort-Object Name -Descending)
foreach ($old in ($withData | Select-Object -Skip 3)) { Remove-Item -LiteralPath (Join-Path $old.FullName 'data') -Recurse -Force }

Write-Host ''
Write-Host "Living Server $build is installed: $($plan.Count) files replaced and checked." -ForegroundColor Green
Write-Host "Backups: $backup"
Write-Host 'ROLLBACK.cmd in this folder puts the replaced files back (it never puts the database back).'
Write-Host 'Start the game as usual; the server log names the build in a FORK_BUILD line.'
if (!$NoPause) { Read-Host 'Press Enter to close' | Out-Null }
exit 0
