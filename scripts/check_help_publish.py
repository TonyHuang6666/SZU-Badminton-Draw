#!/usr/bin/env python3
"""Reject help compiler dependencies/notices in a desktop publish directory."""
import json
import mmap
from pathlib import Path
import sys


def check_publish(root: Path) -> None:
    if not root.is_dir():
        raise ValueError(f"Publish directory does not exist: {root}")
    files = [path for path in root.rglob("*") if path.is_file()]
    for path in files:
        if path.name.casefold().startswith(("markdig.", "badmintondraw.helpcompiler.")):
            raise ValueError(f"Build-only help file in publish output: {path.relative_to(root)}")
    manifests = [path for path in files if path.name.endswith(".deps.json")]
    for path in manifests:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        libraries = data.get("libraries")
        if not isinstance(libraries, dict):
            raise ValueError(f"Invalid dependency manifest: {path}")
        for name in libraries:
            if name.casefold().startswith(("markdig/", "badmintondraw.helpcompiler/")):
                raise ValueError(f"Build-only help dependency in {path.name}: {name}")

    # .NET single-file compression leaves bundled filenames in the manifest
    # uncompressed. Check the actual executable as well as normal publish files.
    bundled = False
    executable = root / "BadmintonDraw.Desktop.exe"
    if executable.is_file() and executable.stat().st_size:
        with executable.open("rb") as source, mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_READ) as content:
            for name in (b"Markdig.dll", b"BadmintonDraw.HelpCompiler.dll", b"ThirdPartyNotices/Markdig.txt",
                         b"ThirdPartyNotices\\Markdig.txt"):
                if content.find(name) >= 0:
                    raise ValueError(f"Build-only help file found in executable: {name.decode()}")
            bundled = all(content.find(name) >= 0 for name in
                          (b"BadmintonDraw.Desktop.dll", b"BadmintonDraw.Desktop.deps.json"))
    if not any(path.name == "BadmintonDraw.Desktop.deps.json" for path in manifests) and not bundled:
        raise ValueError("Desktop dependency manifest or single-file manifest is missing.")


if __name__ == "__main__":
    try:
        if len(sys.argv) != 2:
            raise ValueError("Usage: check_help_publish.py <desktop-publish-directory>")
        check_publish(Path(sys.argv[1]))
    except (ValueError, OSError) as error:
        print(f"Help publish check failed: {error}", file=sys.stderr)
        sys.exit(1)
    print("Help publish check passed: no Markdig runtime, compiler, or notice.")
