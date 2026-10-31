#!/usr/bin/env python3
"""Builds the three release packages of Hearthstone Access Enhanced from this checkout:

    1 Hearthstone access enhanced for Windows.zip
    2 Hearthstone access enhanced for Mac.zip
    3 Hearthstone access enhanced sourcecode.zip

    python3 release/make-packages.py        (Windows: py release\\make-packages.py)

Works the same on Windows and macOS. The zips are written with Unix permissions, so the Mac
installer (.command) and the scripts (.sh) can be run right after unpacking: Archive Utility
keeps them executable and nobody has to type chmod +x. Zips made by Windows (Explorer,
Compress-Archive) carry no permissions, and macOS then refuses to open a .command.

Packaged: the files git tracks (as they are in the working tree), so build output and local
files never end up in a package.
"""
import os
import subprocess
import sys
import time
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RELEASE = os.path.join(ROOT, "release")
EXECUTABLE_SUFFIXES = (".command", ".sh")


def tracked_files():
    """{path: executable} for every file git tracks (paths with forward slashes)."""
    out = subprocess.run(["git", "-C", ROOT, "ls-files", "-s", "-z"], check=True, capture_output=True).stdout
    files = {}
    for entry in out.decode("utf-8").split("\0"):
        if not entry:
            continue
        meta, path = entry.split("\t", 1)
        mode = meta.split(" ")[0]
        if mode == "160000":          # a submodule, not a file
            continue
        files[path] = mode == "100755" or path.endswith(EXECUTABLE_SUFFIXES)
    return files


def add(zf, src_rel, dest, executable):
    src = os.path.join(ROOT, *src_rel.split("/"))
    info = zipfile.ZipInfo(dest, date_time=time.localtime(max(os.path.getmtime(src), 315532800))[:6])
    info.create_system = 3            # Unix: the permissions below are honoured when unpacking
    info.external_attr = (0o100755 if executable else 0o100644) << 16
    info.compress_type = zipfile.ZIP_DEFLATED
    with open(src, "rb") as f:
        zf.writestr(info, f.read())


def package(name, top, files):
    path = os.path.join(RELEASE, name)
    tmp = path + ".tmp"
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as zf:
        for rel, dest, executable in sorted(files, key=lambda f: f[1]):
            add(zf, rel, top + "/" + dest, executable)
    os.replace(tmp, path)
    print("%s: %d files" % (name, len(files)))


def main():
    files = tracked_files()
    missing = [p for p in files if not os.path.isfile(os.path.join(ROOT, *p.split("/")))]
    if missing:
        sys.exit("missing from the working tree: " + ", ".join(missing[:10]))
    resources = [(p, p, x) for p, x in files.items() if p.startswith("Resources/")]

    def top_level(*names):
        return [(n, n, files.get(n, n.endswith(EXECUTABLE_SUFFIXES))) for n in names]

    package("1 Hearthstone access enhanced for Windows.zip", "Hearthstone access for Windows",
            top_level("Install Hearthstone access.bat", "Uninstall Hearthstone access.bat", "LICENSE", "README.txt") + resources)
    package("2 Hearthstone access enhanced for Mac.zip", "Hearthstone access for Mac",
            top_level("Install Hearthstone access.command", "Uninstall Hearthstone access.command", "LICENSE", "README.txt") + resources)
    # the source: everything git tracks except the packages themselves
    source = [(p, p, x) for p, x in files.items() if not (p.startswith("release/") and p.endswith(".zip"))]
    package("3 Hearthstone access enhanced sourcecode.zip", "Hearthstone-access-enhanced-source", source)

    # the Mac installer must come out executable
    with zipfile.ZipFile(os.path.join(RELEASE, "2 Hearthstone access enhanced for Mac.zip")) as zf:
        info = zf.getinfo("Hearthstone access for Mac/Install Hearthstone access.command")
        if not (info.external_attr >> 16) & 0o111:
            sys.exit("the Mac installer is not executable in the package")


if __name__ == "__main__":
    main()
