#!/usr/bin/env python3
"""Build the Living Server package for Offline DAoC True 1.65.

  python -I tools/living-server/make_package.py [--server development-source/server/Release]
      [--launcher development-source/tools/OfflineDaoc.Launcher/bin/Release/net10.0-windows] [--out dist]
      [--commit SHA] [--branch NAME]

Writes <out>/LivingServer-<build>/ and <out>/OfflineDAoC-True165-LivingServer-<build>.zip:

  LivingServer-<build>/
    BUILD-INFO.txt              the build, the release(s) it fits, the SHA-256 of every file it ships and of every
                                released file it replaces (base/<game path>, from release-baselines/<base>/package.sha256)
    server/                     GameServer, CoreBase, CoreDatabase, CoreServer (.dll and .pdb)
    launcher/                   OfflineDAoC.exe, .dll, .pdb, .deps.json, .runtimeconfig.json
    INSTALL.cmd, ROLLBACK.cmd   and their scripts (tools/living-server/package)
    README.txt, docs/           the player's notes

The build name and base release come from ForkBuildInfo.cs, so the package, the server's FORK_BUILD line and the
launcher's update check always agree. Build the solution and the launcher (Release) first. The launcher must be a
Windows build (OfflineDAoC.exe); on another system pass --allow-no-exe for a test package.
"""
import argparse
import hashlib
import os
import re
import shutil
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SERVER_FILES = ["GameServer.dll", "GameServer.pdb", "CoreBase.dll", "CoreBase.pdb", "CoreDatabase.dll", "CoreDatabase.pdb",
                "CoreServer.dll", "CoreServer.pdb"]
LAUNCHER_FILES = ["OfflineDAoC.dll", "OfflineDAoC.exe", "OfflineDAoC.pdb", "OfflineDAoC.deps.json", "OfflineDAoC.runtimeconfig.json"]
SERVER_FOLDERS = ["runtime/server", "runtime/server/lib", "runtime/server/win-x64"]
SCRIPTS = ["INSTALL.cmd", "ROLLBACK.cmd", "install-server-build.ps1", "rollback-server-build.ps1", "fork-build-common.ps1"]
DOCS = ["docs/LIVING-SERVER.md"]


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def build_info():
    text = (REPO / "development-source/server/GameServer/gameutils/ForkBuildInfo.cs").read_text(encoding="utf-8")
    name = re.search(r'BuildName = "([^"]+)"', text).group(1)
    base = re.search(r'BaseRelease = "([^"]+)"', text).group(1)
    return name, [b.strip() for b in base.split(",") if b.strip()]


def baseline(release):
    path = REPO / "release-baselines" / release / "package.sha256"
    if not path.is_file():
        raise SystemExit(f"{path} is missing: this build's BaseRelease {release} has no released baseline")
    hashes = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            digest, name = line.split("  ", 1)
            hashes[name] = digest.lower()
    return hashes


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--server", default=str(REPO / "development-source/server/Release"))
    parser.add_argument("--launcher", default=str(REPO / "development-source/tools/OfflineDaoc.Launcher/bin/Release/net10.0-windows"))
    parser.add_argument("--out", default=str(REPO / "dist"))
    parser.add_argument("--commit", default=os.environ.get("GITHUB_SHA", "local"))
    parser.add_argument("--branch", default=os.environ.get("GITHUB_REF_NAME", ""))
    parser.add_argument("--allow-no-exe", action="store_true", help="test packages built off Windows: ship no OfflineDAoC.exe")
    args = parser.parse_args()

    name, bases = build_info()
    server, launcher, out = Path(args.server), Path(args.launcher), Path(args.out)
    top = out / f"LivingServer-{name}"
    if top.exists():
        shutil.rmtree(top)
    (top / "server").mkdir(parents=True)
    (top / "launcher").mkdir()
    (top / "docs").mkdir()
    info = {"BuildName": name, "BaseRelease": ",".join(bases), "Edition": "true165", "Commit": args.commit,
            "Branch": args.branch, "Built": datetime.now(timezone.utc).isoformat(timespec="seconds")}
    files = {}
    for file in SERVER_FILES:
        source = server / file
        if not source.is_file():
            raise SystemExit(f"{source} is missing: build the solution in Release first")
        shutil.copy2(source, top / "server" / file)
        files[f"server/{file}"] = sha(top / "server" / file)
    for file in LAUNCHER_FILES:
        source = launcher / file
        if not source.is_file():
            if file == "OfflineDAoC.exe" and args.allow_no_exe:
                continue
            raise SystemExit(f"{source} is missing: build the launcher in Release on Windows first")
        shutil.copy2(source, top / "launcher" / file)
        files[f"launcher/{file}"] = sha(top / "launcher" / file)
    for doc in DOCS:
        shutil.copy2(REPO / doc, top / "docs" / Path(doc).name)
        files[f"docs/{Path(doc).name}"] = sha(top / "docs" / Path(doc).name)
    package = REPO / "tools/living-server/package"
    for script in SCRIPTS:
        shutil.copy2(package / script, top / script)
    readme = (package / "README.txt").read_text(encoding="utf-8").replace("{build}", name).replace("{base}", ", v".join(bases))
    (top / "README.txt").write_bytes(readme.replace("\r\n", "\n").replace("\n", "\r\n").encode("utf-8"))

    # The released files this package replaces, for every release it fits (the same path has one hash per release; a
    # build that fits two releases lists the newest release's, and the install also accepts files it installed itself).
    released = {}
    for base in bases:
        hashes = baseline(base)
        for folder in SERVER_FOLDERS:
            for file in SERVER_FILES:
                if f"{folder}/{file}" in hashes:
                    released[f"base/{folder}/{file}"] = hashes[f"{folder}/{file}"]
        for file in LAUNCHER_FILES:
            if f"runtime/{file}" in hashes:
                released[f"base/runtime/{file}"] = hashes[f"runtime/{file}"]
    if "base/runtime/server/GameServer.dll" not in released:
        raise SystemExit("the baseline has no runtime/server/GameServer.dll")

    lines = [f"{k}={v}" for k, v in info.items()] + [f"{k}={v}" for k, v in files.items()] + [f"{k}={v}" for k, v in sorted(released.items())]
    (top / "BUILD-INFO.txt").write_bytes(("\r\n".join(lines) + "\r\n").encode("utf-8"))

    zip_path = out / f"OfflineDAoC-True165-LivingServer-{name}.zip"
    if zip_path.exists():
        zip_path.unlink()
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for path in sorted(top.rglob("*")):
            if path.is_file():
                z.write(path, path.relative_to(out).as_posix())
    print(f"{zip_path} ({zip_path.stat().st_size // 1024} KB, sha256 {sha(zip_path)})")
    print(f"{top}: build {name}, fits v{', v'.join(bases)}, {len(files)} files, replaces {len(released)} released files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
