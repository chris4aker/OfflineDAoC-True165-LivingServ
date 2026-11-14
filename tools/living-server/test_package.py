#!/usr/bin/env python3
"""Tests of the Living Server package's INSTALL and ROLLBACK scripts on made-up True 1.65 game folders.

  python -I tools/living-server/test_package.py        (needs PowerShell: pwsh, or set PWSH to its path)

Each test makes a fake game folder (the start file, VERSION.txt, a server, a launcher and a world database, all small
made-up files), a fake package whose BUILD-INFO.txt names those files as the released ones, and runs the real scripts.
Windows PowerShell 5.1 runs them on players' PCs; these tests run them in PowerShell 7 on any system, which checks the
logic but not 5.1's quirks.
"""
import hashlib
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPTS = HERE / "package"
PWSH = os.environ.get("PWSH") or shutil.which("pwsh") or str(Path.home() / ".dotnet/tools/pwsh")
SERVER = ["GameServer.dll", "GameServer.pdb", "CoreBase.dll", "CoreBase.pdb", "CoreDatabase.dll", "CoreDatabase.pdb", "CoreServer.dll", "CoreServer.pdb"]
LIB = ["GameServer.dll", "GameServer.pdb", "CoreBase.dll", "CoreBase.pdb", "CoreDatabase.dll", "CoreDatabase.pdb"]
LAUNCHER = ["OfflineDAoC.dll", "OfflineDAoC.exe", "OfflineDAoC.pdb", "OfflineDAoC.deps.json", "OfflineDAoC.runtimeconfig.json"]


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def released_game(root):
    game = root / "Offline DAoC True 1.65"
    (game / "runtime/server/lib").mkdir(parents=True)
    (game / "runtime/data").mkdir(parents=True)
    (game / "START OFFLINE DAOC TRUE 1.65.cmd").write_text("@echo off\r\n")
    (game / "VERSION.txt").write_bytes(b"Offline DAoC True 1.65 v0.7")
    for f in SERVER:
        (game / "runtime/server" / f).write_text("released " + f)
    for f in LIB:
        (game / "runtime/server/lib" / f).write_text("released " + f)
    for f in LAUNCHER:
        (game / "runtime" / f).write_text("released " + f)
    (game / "runtime/data/opendaoc-165.sqlite3.db").write_text("my characters")
    return game


def package(root, game, build, tag="", base="0.7"):
    """A package of build whose released files are this game's (as they are now)."""
    top = root / f"pkg-{build}{tag}" / f"LivingServer-{build}"
    (top / "server").mkdir(parents=True)
    (top / "launcher").mkdir()
    for s in ["INSTALL.cmd", "ROLLBACK.cmd", "install-server-build.ps1", "rollback-server-build.ps1", "fork-build-common.ps1"]:
        shutil.copy2(SCRIPTS / s, top / s)
    lines = [f"BuildName={build}", f"BaseRelease={base}"]
    for f in SERVER:
        (top / "server" / f).write_text(f"living server {build}{tag} {f}")
        lines.append(f"server/{f}={sha(top / 'server' / f)}")
    for f in LAUNCHER:
        (top / "launcher" / f).write_text(f"living server {build}{tag} {f}")
        lines.append(f"launcher/{f}={sha(top / 'launcher' / f)}")
    for folder, names in (("runtime/server", SERVER), ("runtime/server/lib", LIB), ("runtime", LAUNCHER)):
        for f in names:
            lines.append(f"base/{folder}/{f}={hashlib.sha256(('released ' + f).encode()).hexdigest()}")
    (top / "BUILD-INFO.txt").write_text("\r\n".join(lines) + "\r\n")
    return top


@unittest.skipUnless(Path(PWSH).exists() or shutil.which(PWSH), "PowerShell (pwsh) is not installed")
class PackageScripts(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp())
        self.game = released_game(self.root)

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def run_script(self, top, script, *extra):
        env = dict(os.environ, DOTNET_ROLL_FORWARD="Major")
        p = subprocess.run([PWSH, "-NoProfile", "-File", str(top / script), "-GameFolder", str(self.game), "-NoPause", *extra],
                           capture_output=True, text=True, env=env, timeout=120)
        return p.returncode, p.stdout + p.stderr

    def snapshot(self):
        return {p.relative_to(self.game).as_posix(): sha(p) for p in self.game.rglob("*")
                if p.is_file() and "deployment-backups" not in p.parts and "docs" not in p.parts}

    def test_installs_checks_and_backs_up(self):
        top = package(self.root, self.game, "1.1.4-beta")
        code, out = self.run_script(top, "install-server-build.ps1")
        self.assertEqual(code, 0, out)
        self.assertIn("19 files replaced", out)
        for f in SERVER:
            self.assertEqual(sha(self.game / "runtime/server" / f), sha(top / "server" / f))
        for f in LIB:
            self.assertEqual(sha(self.game / "runtime/server/lib" / f), sha(top / "server" / f))
        self.assertFalse((self.game / "runtime/server/lib/CoreServer.dll").exists(), "a file only goes where the game has one")
        backups = list((self.game / "runtime/server/deployment-backups").iterdir())
        self.assertEqual(len(backups), 1)
        self.assertEqual((backups[0] / "data/opendaoc-165.sqlite3.db").read_text(), "my characters")
        self.assertEqual((backups[0] / "files/runtime/server/GameServer.dll").read_text(), "released GameServer.dll")
        self.assertTrue((backups[0] / "install-record.json").is_file())
        # A second run has nothing to do.
        code, out = self.run_script(top, "install-server-build.ps1")
        self.assertEqual(code, 0, out)
        self.assertIn("already installed", out)
        self.assertEqual(len(list((self.game / "runtime/server/deployment-backups").iterdir())), 1)

    def test_rollback_puts_the_released_files_back_but_not_the_database(self):
        before = self.snapshot()
        top = package(self.root, self.game, "1.1.4-beta")
        self.assertEqual(self.run_script(top, "install-server-build.ps1")[0], 0)
        (self.game / "runtime/data/opendaoc-165.sqlite3.db").write_text("my characters, a level later")
        code, out = self.run_script(top, "rollback-server-build.ps1")
        self.assertEqual(code, 0, out)
        after = self.snapshot()
        db = "runtime/data/opendaoc-165.sqlite3.db"
        self.assertEqual({k: v for k, v in after.items() if k != db}, {k: v for k, v in before.items() if k != db})
        self.assertEqual((self.game / db).read_text(), "my characters, a level later", "the play since stays")
        code, out = self.run_script(top, "rollback-server-build.ps1")
        self.assertNotEqual(code, 0)
        self.assertIn("no Living Server install to undo", out)

    def test_upgrade_then_two_rollbacks_reach_the_released_game(self):
        before = self.snapshot()
        first = package(self.root, self.game, "1.1.4-beta")
        self.assertEqual(self.run_script(first, "install-server-build.ps1")[0], 0)
        second = package(self.root, self.game, "1.1.5-beta")
        code, out = self.run_script(second, "install-server-build.ps1")
        self.assertEqual(code, 0, out)
        self.assertEqual(sha(self.game / "runtime/server/GameServer.dll"), sha(second / "server/GameServer.dll"))
        self.assertEqual(self.run_script(second, "rollback-server-build.ps1")[0], 0)
        self.assertEqual(sha(self.game / "runtime/server/GameServer.dll"), sha(first / "server/GameServer.dll"))
        self.assertEqual(self.run_script(second, "rollback-server-build.ps1")[0], 0)
        self.assertEqual(self.snapshot(), before)

    def test_a_file_changed_by_something_else_is_refused_unless_forced(self):
        (self.game / "runtime/server/GameServer.dll").write_text("someone's own server")
        top = package(self.root, self.game, "1.1.4-beta")
        before = self.snapshot()
        code, out = self.run_script(top, "install-server-build.ps1")
        self.assertNotEqual(code, 0)
        self.assertIn("NOTHING WAS CHANGED", out)
        self.assertIn("runtime/server/GameServer.dll", out)
        self.assertEqual(self.snapshot(), before)
        code, out = self.run_script(top, "install-server-build.ps1", "-Force")
        self.assertEqual(code, 0, out)
        backup = next((self.game / "runtime/server/deployment-backups").iterdir())
        self.assertEqual((backup / "files/runtime/server/GameServer.dll").read_text(), "someone's own server")

    def test_another_release_is_refused(self):
        (self.game / "VERSION.txt").write_bytes(b"Offline DAoC True 1.65 v0.6")
        top = package(self.root, self.game, "1.1.4-beta")
        before = self.snapshot()
        code, out = self.run_script(top, "install-server-build.ps1")
        self.assertNotEqual(code, 0)
        self.assertIn("v0.6", out)
        self.assertEqual(self.snapshot(), before)

    def test_a_damaged_package_is_refused(self):
        top = package(self.root, self.game, "1.1.4-beta")
        (top / "server/GameServer.dll").write_text("damaged in the download")
        before = self.snapshot()
        code, out = self.run_script(top, "install-server-build.ps1")
        self.assertNotEqual(code, 0)
        self.assertIn("Download it again", out)
        self.assertEqual(self.snapshot(), before)

    def test_a_folder_that_isnt_a_true165_game_is_refused(self):
        (self.game / "START OFFLINE DAOC TRUE 1.65.cmd").unlink()
        top = package(self.root, self.game, "1.1.4-beta")
        code, out = self.run_script(top, "install-server-build.ps1")
        self.assertNotEqual(code, 0)
        self.assertIn("not an Offline DAoC True 1.65 game folder", out)


if __name__ == "__main__":
    unittest.main(verbosity=2)
