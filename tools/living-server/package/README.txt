LIVING SERVER FOR OFFLINE DAOC TRUE 1.65 - BUILD {build}   (github.com/chris4aker/OfflineDAoC-True165-LivingServ)
====================================================================================================

An add-on for Offline DAoC True 1.65 v{base} that makes its gamebots feel like 2003 players: they talk in
their own voices, argue in realm chat, remember you, congratulate your levels and tell tales of what you did.
It replaces the server (GameServer.dll and its libraries CoreBase, CoreDatabase and CoreServer) and the
launcher (OfflineDAoC.exe, which gets a Living Server tab, an update check and the Camelot Herald page).
Nothing else in the game changes. With every part switched off in the Living Server tab, the game plays
exactly like True 1.65.

INSTALL
-------
The easy way is LivingServer-True165-Setup.exe from the release page: it gets the game too if you don't
have it. By hand:
1. Stop the server, close the launcher and the game.
2. Put this folder INSIDE your True 1.65 game folder (the one with START OFFLINE DAOC TRUE 1.65.cmd).
3. Double-click INSTALL.cmd. If Windows warns about an unknown app: More info -> Run anyway.

INSTALL.cmd checks the game is v{base}, refuses while anything runs, backs up your world database and every
file it replaces (runtime\server\deployment-backups\livingserver-<build>-<time>) and checks every copy.
The newest three installs keep their database copy.

Check: start the game as usual. The server log has a line like
  FORK_BUILD name={build} edition=true165 base={base} ...

UNDO
----
ROLLBACK.cmd puts back the files the newest install replaced (run it again to go back one build further).
It never puts the world database back, as that would undo your play since; the copy is in the backup's
data folder if you want it.

UPDATING TRUE 1.65 ITSELF
-------------------------
The True 1.65 updater replaces only the released files. Before you update the game, run ROLLBACK.cmd
(until it says there is nothing left to undo), update the game, then install the Living Server build made
for the new version. Your characters, bots and Living Server settings stay in the world database.
