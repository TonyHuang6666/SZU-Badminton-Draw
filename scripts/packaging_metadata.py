#!/usr/bin/env python3
"""Validated version lookup and macOS package preflight (Python standard library).

The shell owns publishing, icons and hdiutil. This helper never deletes outputs.
"""

from datetime import datetime, timezone
import json
import os
from pathlib import Path
import plistlib
import re
import stat
import subprocess
import sys
import uuid


def validate_version(value):
    # One numeric release version is also valid for both CFBundle version fields.
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)", value):
        raise ValueError("Version must be one numeric major.minor.patch value, without whitespace or a suffix.")
    if any(int(part) > 65534 for part in value.split(".")):
        raise ValueError("Version components must fit the .NET assembly version range (0–65534).")
    return value


def validate_configuration(value):
    if value not in ("Release", "Debug"):
        raise ValueError("CONFIGURATION must be Release or Debug.")
    return value


def evaluated_version(project, configuration):
    validate_configuration(configuration)
    result = subprocess.run(
        ["dotnet", "msbuild", str(project), "-nologo", "-getProperty:VersionPrefix",
         f"-p:Configuration={configuration}"], check=True, capture_output=True, text=True)
    # Remove only the tool's line terminator; banners, multiple values and whitespace fail closed.
    return validate_version(result.stdout.rstrip("\r\n"))


def validate_app_name(value):
    if (not value or value != value.strip() or value in (".", "..") or value.startswith("-")
            or any(char in "/\\:\x85\u2028\u2029" or ord(char) < 32 or ord(char) == 127
                   or 0xFDD0 <= ord(char) <= 0xFDEF or (ord(char) & 0xFFFF) in (0xFFFE, 0xFFFF)
                   for char in value)
            or len(value.encode("utf-8")) > 200):
        raise ValueError("APP_NAME must be a nonempty filename, without paths, control characters or edge whitespace.")


def source_identity(root):
    commit = subprocess.run(["git", "-C", str(root), "rev-parse", "--verify", "HEAD"],
                            check=True, capture_output=True, text=True).stdout.strip()
    if not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", commit):
        raise ValueError("Unable to capture the full Git source commit.")
    status = subprocess.run(["git", "-C", str(root), "status", "--porcelain", "--untracked-files=normal"],
                            check=True, capture_output=True, text=True).stdout
    return commit, bool(status)


def claim_run_directory(root, rid, version):
    parts = ("artifacts", "macos", rid, version)
    path = root
    # Inspect the entire existing fixed chain before making even the first directory.
    for part in parts:
        path = path / part
        try:
            mode = path.lstat().st_mode
        except FileNotFoundError:
            continue
        if not stat.S_ISDIR(mode):
            raise ValueError(f"Packaging output parent is not a real directory (symlinks are forbidden): {path}")

    # Recheck each component without following links while creating missing parents.
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    descriptor = os.open(root, flags)
    try:
        for part in parts:
            try:
                os.mkdir(part, dir_fd=descriptor)
            except FileExistsError:
                pass
            child = os.open(part, flags, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
        name = "run-" + uuid.uuid4().hex
        os.mkdir(name, mode=0o700, dir_fd=descriptor)  # atomic claim; never reuse or overwrite
    finally:
        os.close(descriptor)
    return path / name


def prepare_macos(arguments):
    if len(arguments) != 8:
        raise ValueError("Invalid macOS preflight arguments.")
    root_text, rid, configuration, app_name, bundle_id, override_set, override, icon = arguments
    root = Path(root_text).resolve(strict=True)
    if any(ord(char) < 32 or ord(char) == 127 for char in str(root)):
        raise ValueError("The project path must not contain control characters.")
    if rid not in ("osx-arm64", "osx-x64"):
        raise ValueError("Supported macOS RIDs are osx-arm64 and osx-x64.")
    validate_configuration(configuration)
    validate_app_name(app_name)
    bundle_parts = bundle_id.split(".")
    if len(bundle_parts) < 2 or not all(
            re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?", part) for part in bundle_parts):
        raise ValueError("BUNDLE_ID must be a dotted identifier containing only ASCII letters, digits and hyphens.")
    if override_set not in ("", "x") or icon not in ("", "AppIcon"):
        raise ValueError("Invalid preflight option.")
    project = root / "src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj"
    version = validate_version(override) if override_set else evaluated_version(project, configuration)
    commit, dirty = source_identity(root)  # any Git failure aborts; never invent a clean commit
    run = claim_run_directory(root, rid, version)
    print(f"Claimed packaging output (retained on failure): {run}", file=sys.stderr)
    contents = run / "dmg-root" / (app_name + ".app") / "Contents"
    resources = contents / "Resources"
    resources.mkdir(parents=True)
    info = {
        "CFBundleDevelopmentRegion": "zh_CN", "CFBundleDisplayName": app_name,
        "CFBundleExecutable": "BadmintonDraw.Desktop", "CFBundleIdentifier": bundle_id,
        "CFBundleInfoDictionaryVersion": "6.0", "CFBundleName": app_name,
        "CFBundlePackageType": "APPL", "CFBundleShortVersionString": version,
        "CFBundleVersion": version, "LSMinimumSystemVersion": "12.0",
        "NSHighResolutionCapable": True,
    }
    if icon:
        info["CFBundleIconFile"] = icon
    with (contents / "Info.plist").open("xb") as output:
        plistlib.dump(info, output, sort_keys=False)
    metadata = {
        "version": version, "versionSource": "VERSION override" if override_set else "evaluated VersionPrefix",
        "sourceCommit": commit, "sourceDirty": dirty, "sourceSnapshot": "working-tree at preflight",
        "configuration": configuration, "runtimeIdentifier": rid,
        "capturedAtUtc": datetime.now(timezone.utc).isoformat(),
    }
    with (resources / "build-metadata.json").open("x", encoding="utf-8") as output:
        json.dump(metadata, output, ensure_ascii=False, indent=2)
        output.write("\n")
    print(version)
    print(run)


def main():
    if len(sys.argv) == 4 and sys.argv[1] == "version":
        print(evaluated_version(Path(sys.argv[2]).resolve(strict=True), sys.argv[3]))
    elif len(sys.argv) > 1 and sys.argv[1] == "prepare-macos":
        prepare_macos(sys.argv[2:])
    else:
        raise ValueError("Usage: packaging_metadata.py version PROJECT CONFIGURATION | prepare-macos ...")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"Packaging preflight failed: {error}", file=sys.stderr)
        sys.exit(1)
