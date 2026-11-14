# Living Server for Offline DAoC True 1.65: shared by install-server-build.ps1 and rollback-server-build.ps1.
# Windows PowerShell 5.1 compatible (and PowerShell 7, which the tests use).

# What the package replaces, as "package path" = "game path". A server file goes into each server folder that has it
# (runtime\server and runtime\server\lib; win-x64 is a copy the True 1.65 release leaves out, used only where present).
$Script:ServerFolders = @('runtime/server', 'runtime/server/lib', 'runtime/server/win-x64')
$Script:ServerFiles = @('GameServer.dll', 'GameServer.pdb', 'CoreBase.dll', 'CoreBase.pdb', 'CoreDatabase.dll', 'CoreDatabase.pdb',
    'CoreServer.dll', 'CoreServer.pdb')
$Script:LauncherFiles = @('OfflineDAoC.dll', 'OfflineDAoC.exe', 'OfflineDAoC.pdb', 'OfflineDAoC.deps.json', 'OfflineDAoC.runtimeconfig.json')
# The server, the launcher and the new client (its Godot runner), which use these files or the world database.
$Script:ProcessNames = @('CoreServer', 'DOLServer', 'OfflineDAoC', 'Godot_v4.7.2-stable_mono_win64')
$Script:DatabaseName = 'opendaoc-165.sqlite3.db'
$Script:BackupRoot = 'runtime/server/deployment-backups'

function Stop-WithMessage([string]$Message) {
    Write-Host ''
    Write-Host "NOTHING WAS CHANGED: $Message" -ForegroundColor Red
    exit 1
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Join-Game([string]$Game, [string]$Relative) {
    $path = $Game
    foreach ($part in ($Relative -split '[\\/]')) { if ($part) { $path = Join-Path $path $part } }
    return $path
}

function Read-KeyValueFile([string]$Path) {
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^\s*([^=#]+?)\s*=\s*(.*?)\s*$') { $values[$Matches[1]] = $Matches[2] }
    }
    return $values
}

# A True 1.65 game folder: its start file and its server.
function Test-GameFolder([string]$Path) {
    return $Path -and (Test-Path -LiteralPath (Join-Path $Path 'START OFFLINE DAOC TRUE 1.65.cmd')) -and
        (Test-Path -LiteralPath (Join-Game $Path 'runtime/server/GameServer.dll'))
}

# The game folder: the one given, else the folder this package sits in or its parent.
function Find-GameFolder([string]$Given, [string]$BuildDir) {
    if ($Given) {
        $full = [IO.Path]::GetFullPath($Given)
        if (Test-GameFolder $full) { return $full }
        Stop-WithMessage "$full is not an Offline DAoC True 1.65 game folder (it needs START OFFLINE DAOC TRUE 1.65.cmd and runtime\server\GameServer.dll)."
    }
    $parent = Split-Path -Parent $BuildDir
    foreach ($candidate in @($parent, (Split-Path -Parent $parent))) {
        if ($candidate -and (Test-GameFolder $candidate)) { return $candidate }
    }
    Stop-WithMessage ("Put this folder inside your Offline DAoC True 1.65 game folder (the one with START OFFLINE DAOC TRUE 1.65.cmd), " +
        "or run INSTALL.cmd -GameFolder ""C:\path\to\the\game"".")
}

# The game's release: "Offline DAoC True 1.65 v0.7" in VERSION.txt -> "0.7".
function Get-GameRelease([string]$Game) {
    $file = Join-Path $Game 'VERSION.txt'
    if (!(Test-Path -LiteralPath $file)) { return '' }
    $text = (Get-Content -LiteralPath $file -Raw)
    if ($text -match 'v(\d+(?:\.\d+)+)') { return $Matches[1] }
    return ''
}

function Assert-GameClosed {
    $running = @(Get-Process -Name $Script:ProcessNames -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $names = ($running | ForEach-Object { $_.ProcessName } | Select-Object -Unique) -join ', '
        Stop-WithMessage "close the game first: $names is running. Stop the server in the launcher, quit the game and close the launcher."
    }
}

# Every file this package replaces: package file, game file, the package's SHA-256, the released file's SHA-256 (from
# BUILD-INFO.txt, "base/<game path>=<sha>"), for the folders the game has.
function Get-Targets([string]$Game, [string]$BuildDir, $Info) {
    $targets = @()
    foreach ($folder in $Script:ServerFolders) {
        foreach ($name in $Script:ServerFiles) {
            $gamePath = "$folder/$name"
            if (!$Info["server/$name"] -or !(Test-Path -LiteralPath (Join-Game $Game $gamePath))) { continue }
            $targets += [pscustomobject]@{ Package = "server/$name"; Game = $gamePath; Sha = $Info["server/$name"]; Base = $Info["base/$gamePath"] }
        }
    }
    foreach ($name in $Script:LauncherFiles) {
        if (!$Info["launcher/$name"]) { continue }
        $targets += [pscustomobject]@{ Package = "launcher/$name"; Game = "runtime/$name"; Sha = $Info["launcher/$name"]; Base = $Info["base/runtime/$name"] }
    }
    return $targets
}

function Get-InstallRecords([string]$Game) {
    $root = Join-Game $Game $Script:BackupRoot
    if (!(Test-Path -LiteralPath $root)) { return @() }
    $records = @()
    foreach ($folder in Get-ChildItem -LiteralPath $root -Directory | Sort-Object Name) {
        $file = Join-Path $folder.FullName 'install-record.json'
        if (Test-Path -LiteralPath $file) {
            $record = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
            $record | Add-Member -NotePropertyName Folder -NotePropertyValue $folder.FullName -Force
            $records += $record
        }
    }
    return $records
}
