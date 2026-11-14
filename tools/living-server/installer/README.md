# Living Server Setup for Offline DAoC True 1.65

One exe that gets a player from nothing to playing Offline DAoC True 1.65 with Living Server, or adds Living Server to
the True 1.65 game they already have.

| The player has | Setup does |
|---|---|
| Nothing | Downloads True 1.65 (the release this build fits, with its new client) with the game's own downloader, then installs Living Server |
| True 1.65 at that release | Installs Living Server |
| An earlier Living Server build on it | Upgrades it (the package's installer recognises its own earlier builds by their install records) |
| An older True 1.65 the game's update starts from | Takes Living Server off if it's there, updates the game in place with the game's own update (saves stay), installs Living Server |
| An older True 1.65 the update doesn't start from | Offers a new install |
| This Living Server build | Checks it (repairs anything missing), or removes Living Server |

It also adds desktop and Start menu shortcuts (optional).

A first install of Living Server ticks "Turn on Living Server's suggested settings": Setup runs the installed
launcher's `OfflineDAoC.exe --apply-recommended-settings`, which saves `SocialExpansionSettings.Recommended` through
the Living Server tab's own save. In this edition that is bot personalities, voices, debates and server life; anything
that changes a True 1.65 rule stays off.

## How it works

Setup doesn't reimplement anything that already has a tested script. It embeds two payloads and runs them:

- **the game's own downloader**, `release/Get-OfflineDAoC-True165.ps1`, unchanged and pinned by SHA-256 in `build.py`.
  Every game file comes from the game's GitHub releases, checked part by part against its `download-manifest.json`.
  Setup runs it with `-Mode install -Yes -NoShortcut -Destination <folder>` for a new game and
  `-Mode update -Yes -NoShortcut -Update <folder>` for an older one. Its cache goes to
  `Offline DAoC True 1.65 setup files\.downloads` beside the game folder; a stopped new install continues where it
  stopped (the downloader marks each unpacked part in the game's `.install` folder).
- **the Living Server package** (`tools/living-server/make_package.py`; its `BUILD-INFO.txt` names the build and the
  release it fits). Setup unpacks it into the game folder, as a player would, and runs its `install-server-build.ps1`
  and `rollback-server-build.ps1`, so every check stays: the release check, the refusal while the game runs, the world
  database backup, backups of every replaced file, the hash-checked copies, and `ROLLBACK.cmd`.

Setup only adds a progress view over them: it reads the downloader's manifest and part markers for real byte counts,
reads the scripts' output for the current step, and turns refusals into plain choices ("Replace anyway", "Fresh copy
+ my save", "Choose another folder").

Two things differ from the downloader run by hand:
- `-Yes` would answer the downloader's "make a fresh install and bring your save over?" itself when the update finds
  changed game files. Setup stops it there and asks the player instead (a fresh copy is the full download).
- Setup refuses a new-install folder under Program Files, in OneDrive, with letters outside plain English, or longer
  than 42 characters, where the downloader only warns: with `-Yes` nobody would read the warning.

Stopping is offered only during a new install's download (parts are kept and continued). An update to the player's own
game is never stopped part-way.

## Build

```
python -I tools/living-server/make_package.py               # the package (build the solution and the launcher first)
python -I tools/living-server/installer/build.py --package dist/OfflineDAoC-True165-LivingServer-<build>.zip
python -I tools/living-server/installer/build.py --release v<build>    # a published release's package
```

Needs the .NET SDK (it targets .NET Framework 4.7.2 through reference assemblies, so it builds on Windows or Linux).
Output: `dist/LivingServer-True165-Setup-<build>.exe` (about 11 MB, the package included). A package for players must
be built on Windows, so it carries the launcher's `OfflineDAoC.exe`; `build.py` warns when it doesn't.

The art: `python tools/living-server/installer/art/make_art.py --launcher-art <folder>` rebuilds it from the Offline
DAoC launcher's art sources (in the Living Server repository's `source/tools/OfflineDaoc.Launcher/Assets`) and Living
Server's splash painting, with the lettering in Cinzel (SIL Open Font License).

## Command line (tests and support)

```
LivingServerSetup-True165.exe --silent --new [--dest DIR]
LivingServerSetup-True165.exe --silent --game DIR [--remove] [--force] [--fresh-copy]
    [--no-shortcuts] [--recommended-settings | --no-recommended-settings] [--local-assets DIR] [--no-update] [--log FILE]
LivingServerSetup-True165.exe --scan [--game DIR]        # what Setup makes of the games it finds
LivingServerSetup-True165.exe --info [--no-update]       # which package it would install
LivingServerSetup-True165.exe --screenshots DIR          # every page with made-up data
```

`--local-assets DIR` makes the downloader read release files from `DIR\v<release>\` instead of GitHub.

## What has been checked

- Builds on Linux (net472 reference assemblies), with the package and the pinned downloader embedded.
- The package's scripts: `tools/living-server/test_package.py` (PowerShell 7, made-up game folders).
- Not yet: Setup itself running on Windows (the window, a new install, an update, a removal). The 0.37 Setup's Windows
  test script has not been ported yet.
