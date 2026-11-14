"""Builds Living Server Setup for Offline DAoC True 1.65: one exe that installs the game and Living Server.

    python -I tools/living-server/installer/build.py --package dist/OfflineDAoC-True165-LivingServer-<build>.zip
    python -I tools/living-server/installer/build.py --release v1.1.4-beta     # downloads that release's package

Writes dist/LivingServer-True165-Setup-<build>.exe (needs the .NET SDK; builds on Windows or Linux).

The exe carries two payloads:
- the Living Server package (tools/living-server/make_package.py; its BUILD-INFO.txt names the build and the True 1.65
  release it fits);
- the game's own downloader, release/Get-OfflineDAoC-True165.ps1 (shadowofze's, unchanged), pinned below by SHA-256
  so a changed downloader can't slip in unnoticed.
"""
import argparse
import hashlib
import shutil
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
PAYLOAD = HERE / "payload"
DIST = ROOT / "dist"
REPO = "chris4aker/OfflineDAoC-True165-LivingServ"
DOWNLOADER = ROOT / "release" / "Get-OfflineDAoC-True165.ps1"

# release/Get-OfflineDAoC-True165.ps1 as in shadowofze's repository at f68cc75 (v0.7, release links for zomboden), LF line ends, checked 2026-11-14.
# Any change to the file fails the build: check what changed, then update this.
DOWNLOADER_SHA256 = "21328d0b472834784289360cd4fa3164851ffde61cc380fd990ea144540b331c"
# What each package must carry, as BUILD-INFO.txt keys and as files.
REQUIRED_INFO = ("BuildName", "BaseRelease", "Edition", "server/GameServer.dll", "server/GameServer.pdb", "base/runtime/server/GameServer.dll")
REQUIRED_FILES = ("install-server-build.ps1", "rollback-server-build.ps1", "fork-build-common.ps1", "INSTALL.cmd", "ROLLBACK.cmd",
                  "server/GameServer.dll")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def fetch_release(tag):
    name = f"OfflineDAoC-True165-LivingServer-{tag.lstrip('v')}.zip"
    url = f"https://github.com/{REPO}/releases/download/{tag}/{name}"
    dest = PAYLOAD / "download" / name
    dest.parent.mkdir(parents=True, exist_ok=True)
    if not dest.exists():
        print(f"Downloading {url}")
        part = dest.with_suffix(".part")
        with urllib.request.urlopen(url) as r, open(part, "wb") as f:
            shutil.copyfileobj(r, f)
        part.replace(dest)   # only a complete download gets the real name
    return dest


def check_package(path):
    with zipfile.ZipFile(path) as z:
        infos = [n for n in z.namelist() if n.count("/") == 1 and n.endswith("/BUILD-INFO.txt")]
        if len(infos) != 1:
            sys.exit(f"{path} is not a Living Server package (no BUILD-INFO.txt at its top folder).")
        root = infos[0].split("/")[0]
        info = dict(l.split("=", 1) for l in z.read(infos[0]).decode("utf-8-sig").splitlines() if "=" in l)
        for key in REQUIRED_INFO:
            if not info.get(key):
                sys.exit(f"{path}: BUILD-INFO.txt has no {key}.")
        if info["Edition"] != "true165":
            sys.exit(f"{path} is a Living Server package for {info['Edition']}, not for True 1.65.")
        for needed in REQUIRED_FILES:
            if f"{root}/{needed}" not in z.namelist():
                sys.exit(f"{path} has no {needed}.")
        for key, digest in info.items():
            if key.split("/")[0] in ("server", "launcher", "docs"):
                entry = f"{root}/{key}"
                if entry not in z.namelist():
                    sys.exit(f"{path}: BUILD-INFO.txt names {key}, which isn't in the package.")
                if hashlib.sha256(z.read(entry)).hexdigest() != digest.lower():
                    sys.exit(f"{path}: {key} doesn't match its BUILD-INFO.txt.")
        if f"{root}/launcher/OfflineDAoC.exe" not in z.namelist():
            print(f"WARNING: {path.name} has no launcher/OfflineDAoC.exe (a test package built off Windows): "
                  "Setup installs the released launcher's exe with this package's launcher code.")
    return info


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--package", help="a Living Server package zip (tools/living-server/make_package.py)")
    ap.add_argument("--release", help="a release tag of this repository to download the package from")
    ap.add_argument("--no-build", action="store_true", help="only stage the payloads")
    a = ap.parse_args()
    if not a.package and not a.release:
        ap.error("give --package or --release")

    PAYLOAD.mkdir(parents=True, exist_ok=True)
    package = Path(a.package) if a.package else fetch_release(a.release)
    info = check_package(package)
    shutil.copyfile(package, PAYLOAD / "LivingServer.zip")
    print(f"Package: {package.name}  build {info['BuildName']} for True 1.65 v{info['BaseRelease']}  sha256 {sha256(package)}")

    # Compared and embedded with LF line ends, as it is published: a Windows checkout may turn them into CRLF.
    data = DOWNLOADER.read_bytes().replace(b"\r\n", b"\n")
    got = hashlib.sha256(data).hexdigest()
    if got != DOWNLOADER_SHA256:
        sys.exit(f"{DOWNLOADER.name} is not the pinned downloader (sha256 {got}). "
                 "If it was changed on purpose, check the change and update DOWNLOADER_SHA256.")
    if f"$ReleaseVersion = '{info['BaseRelease'].split(',')[-1].strip()}'" not in data.decode("utf-8-sig"):
        sys.exit(f"{DOWNLOADER.name} isn't the downloader of v{info['BaseRelease']}, the release this package fits.")
    (PAYLOAD / DOWNLOADER.name).write_bytes(data)
    print(f"Downloader: {DOWNLOADER.name}  sha256 {got} (pinned)")
    if a.no_build:
        return

    proj = HERE / "src" / "LivingServerSetup.csproj"
    out = HERE / "src" / "bin" / "Release" / "net472"
    subprocess.run(["dotnet", "build", str(proj), "-c", "Release", "-nologo", "-v", "minimal",
                    f"-p:PayloadDir={PAYLOAD}{'/' if not str(PAYLOAD).endswith('/') else ''}"], check=True)
    DIST.mkdir(exist_ok=True)
    exe = DIST / f"LivingServer-True165-Setup-{info['BuildName']}.exe"
    shutil.copyfile(out / "LivingServerSetup-True165.exe", exe)
    print(f"Built {exe.relative_to(ROOT)}  {exe.stat().st_size / 1048576:.1f} MB  sha256 {sha256(exe)}")


if __name__ == "__main__":
    main()
