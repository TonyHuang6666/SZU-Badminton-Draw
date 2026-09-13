#!/usr/bin/env python3
"""Isolated script safety tests. Fake publish/DMG tools are NOT release evidence."""

import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import tempfile
import unittest


SCRIPTS = Path(__file__).resolve().parent
FAKE_TOOL = r'''#!/usr/bin/env python3
import json, os, pathlib, sys
import xml.etree.ElementTree as ET
tool = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
with open(os.environ["PACKAGING_TEST_LOG"], "a") as log:
    log.write(json.dumps([tool, *args]) + "\n")
if tool == "uname":
    print("Darwin")
elif tool == "dotnet":
    if args[0] == "msbuild":
        props = pathlib.Path(args[1]).parents[2] / "Directory.Build.props"
        print(os.environ.get("PACKAGING_TEST_VERSION", ET.parse(props).findtext(".//VersionPrefix")))
    elif args[0] == "publish":
        target = pathlib.Path(args[args.index("-o") + 1])
        target.mkdir(parents=True, exist_ok=True)
        (target / "BadmintonDraw.Desktop").write_text("MOCK EXECUTABLE ONLY\n")
        (target / "libe_sqlite3.dylib").write_bytes(b"MOCK NATIVE ASSET\n")
        if os.environ.get("PACKAGING_TEST_FAIL_STAGE") == "publish":
            sys.exit(84)
    else:
        sys.exit(90)
elif tool == "hdiutil":
    if args[0] == "create":
        pathlib.Path(args[-1]).write_bytes(b"MOCK DMG ONLY\n")
    elif args[0] == "verify":
        assert pathlib.Path(args[1]).read_bytes() == b"MOCK DMG ONLY\n"
        if os.environ.get("PACKAGING_TEST_FAIL_STAGE") == "verify":
            sys.exit(85)
    else:
        sys.exit(91)
elif tool in ("sips", "iconutil"):
    flag = "--out" if tool == "sips" else "-o"
    pathlib.Path(args[args.index(flag) + 1]).write_bytes(b"MOCK ICON ONLY\n")
'''


class Fixture:
    def __enter__(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="szbd packaging fixture ")
        self.base = Path(self.temporary.name).resolve()
        self.repo = self.base / "repo with spaces"
        self.bin = self.base / "fake-bin"
        self.log = self.base / "tool-calls.jsonl"
        (self.repo / "scripts").mkdir(parents=True)
        self.bin.mkdir()
        for name in ("publish-macos.sh", "packaging_metadata.py"):
            if (SCRIPTS / name).is_file():
                shutil.copy2(SCRIPTS / name, self.repo / "scripts" / name)
        project = self.repo / "src/BadmintonDraw.Desktop"
        (project / "Assets").mkdir(parents=True)
        (project / "BadmintonDraw.Desktop.csproj").write_text("<Project />\n")
        (project / "Assets/szuba-app-icon.png").write_bytes(b"MOCK INPUT ICON\n")
        (self.repo / "Directory.Build.props").write_text(
            "<Project><PropertyGroup><VersionPrefix>5.7.9</VersionPrefix></PropertyGroup></Project>\n")
        (self.repo / ".gitignore").write_text("artifacts/\n")
        for name in ("uname", "dotnet", "hdiutil", "sips", "iconutil"):
            target = self.bin / name
            target.write_text(FAKE_TOOL)
            target.chmod(0o755)
        self.env = os.environ.copy()
        for name in ("APP_NAME", "BUNDLE_ID", "VERSION", "CONFIGURATION"):
            self.env.pop(name, None)
        self.env.update(PATH=str(self.bin) + os.pathsep + self.env["PATH"],
                        PACKAGING_TEST_LOG=str(self.log))
        self.git("init", "-q")
        self.git("-c", "user.name=Packaging Test", "-c", "user.email=fixture@example.invalid",
                 "add", ".")
        self.git("-c", "user.name=Packaging Test", "-c", "user.email=fixture@example.invalid",
                 "commit", "-qm", "isolated packaging fixture")
        self.commit = self.git("rev-parse", "HEAD").strip()
        return self

    def __exit__(self, *args):
        self.temporary.cleanup()

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.repo), *args], text=True)

    def run(self, *args, **env):
        return subprocess.run(["/bin/bash", str(self.repo / "scripts/publish-macos.sh"), *args],
                              env={**self.env, **env}, capture_output=True, text=True, timeout=30)

    def calls(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def snapshot(self):
        # Never follow symlinks into another tree while checking no output write.
        root = self.repo / "artifacts"
        entries = {}
        if root.is_symlink():
            return {".": ("link", os.readlink(root))}
        if root.exists():
            for directory, dirs, files in os.walk(root, followlinks=False):
                for name in dirs + files:
                    item = Path(directory) / name
                    entries[str(item.relative_to(root))] = (
                        ("link", os.readlink(item)) if item.is_symlink() else
                        ("file", item.read_bytes()) if item.is_file() else ("dir",))
        return entries


class PackagingPreflightTests(unittest.TestCase):
    def assert_rejected_without_output(self, fixture, args=(), env=None):
        before = fixture.snapshot()
        result = fixture.run(*args, **(env or {}))
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertEqual(fixture.snapshot(), before, result.stdout + result.stderr)
        self.assertFalse(any(call[:2] == ["dotnet", "publish"] for call in fixture.calls()))

    def test_invalid_inputs_do_not_publish_or_change_existing_evidence(self):
        # Removing early input validation would execute publish or remove the sentinel.
        cases = [
            (("../escape",), {}), (("../../escape",), {}), (("",), {}),
            (("/absolute",), {}), (("win-x64",), {}), (("osx-arm64", "extra"), {}),
            ((), {"APP_NAME": ""}), ((), {"APP_NAME": "../bad"}),
            ((), {"APP_NAME": "bad\\name"}), ((), {"APP_NAME": "bad\nname"}),
            ((), {"APP_NAME": "/absolute"}), ((), {"APP_NAME": ".."}),
            ((), {"VERSION": ""}), ((), {"VERSION": "5.0.0\n6.0.0"}),
            ((), {"VERSION": "../5.0.0"}), ((), {"VERSION": "v5.0.0"}),
            ((), {"VERSION": "5.0.0-beta"}), ((), {"VERSION": "05.0.0"}),
            ((), {"CONFIGURATION": "../Release"}), ((), {"CONFIGURATION": ""}),
            ((), {"BUNDLE_ID": "com.example</string><true/>"}),
            ((), {"BUNDLE_ID": ""}),
        ]
        for args, env in cases:
            with self.subTest(args=args, env=env), Fixture() as fixture:
                for directory in ("artifacts/macos/osx-arm64", "artifacts/escape", "escape"):
                    target = fixture.repo / directory
                    target.mkdir(parents=True)
                    (target / "sentinel").write_bytes(b"retain original evidence")
                self.assert_rejected_without_output(fixture, args, env)
                self.assertEqual((fixture.repo / "escape/sentinel").read_bytes(), b"retain original evidence")

    def test_each_fixed_output_parent_rejects_symlink_before_writing(self):
        # Following any parent symlink could write into another release/evidence directory.
        for parent in ("artifacts", "artifacts/macos", "artifacts/macos/osx-arm64",
                       "artifacts/macos/osx-arm64/5.7.9"):
            with self.subTest(parent=parent), Fixture() as fixture:
                outside = fixture.base / "preserved-evidence"
                outside.mkdir()
                sentinel = outside / "sentinel"
                sentinel.write_bytes(b"never touch symlink destination")
                link = fixture.repo / parent
                link.parent.mkdir(parents=True, exist_ok=True)
                link.symlink_to(outside, target_is_directory=True)
                self.assert_rejected_without_output(fixture)
                self.assertEqual(list(outside.iterdir()), [sentinel])
                self.assertEqual(sentinel.read_bytes(), b"never touch symlink destination")

    def test_unresolvable_source_is_not_packaged_as_clean(self):
        with Fixture() as fixture:
            fixture.git("update-ref", "-d", "HEAD")
            self.assert_rejected_without_output(fixture)

    def test_invalid_evaluated_version_rejects_before_output(self):
        with Fixture() as fixture:
            self.assert_rejected_without_output(fixture, env={"PACKAGING_TEST_VERSION": "5.7.9\nwarning"})

    def test_xml_noncharacters_in_name_reject_before_creating_invalid_plist(self):
        for value in ("bad\ufffename", "bad\uffffname"):
            with self.subTest(value=value), Fixture() as fixture:
                self.assert_rejected_without_output(fixture, env={"APP_NAME": value})

    def test_unicode_line_breaks_in_app_name_reject_before_output(self):
        for value in ("bad\x85name", "bad\u2028name", "bad\u2029name"):
            with self.subTest(value=value), Fixture() as fixture:
                self.assert_rejected_without_output(fixture, env={"APP_NAME": value})

    def test_missing_python_or_hdiutil_fails_before_creating_output(self):
        for missing in ("python3", "hdiutil"):
            with self.subTest(missing=missing), Fixture() as fixture:
                minimal_bin = fixture.base / "minimal-bin"
                minimal_bin.mkdir()
                # Keep real shell filesystem commands; absence of those must not hide a late write.
                for name in ("dirname", "mkdir", "cp", "chmod", "ln", "git", "python3"):
                    if name != missing:
                        (minimal_bin / name).symlink_to(shutil.which(name))
                (minimal_bin / "uname").write_text("#!/bin/sh\nprintf 'Darwin\\n'\n")
                (minimal_bin / "uname").chmod(0o755)
                for name in ("dotnet", "hdiutil"):
                    if name != missing:
                        (minimal_bin / name).symlink_to(fixture.bin / name)
                self.assert_rejected_without_output(fixture, env={"PATH": str(minimal_bin)})

    def test_publish_or_verify_failure_retains_partial_run_without_success_claim(self):
        for stage in ("publish", "verify"):
            with self.subTest(stage=stage), Fixture() as fixture:
                result = fixture.run(PACKAGING_TEST_FAIL_STAGE=stage)
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertNotIn("Created DMG:", result.stdout)
                retained = list((fixture.repo / "artifacts/macos/osx-arm64/5.7.9").glob("run-*"))
                self.assertEqual(len(retained), 1)
                self.assertIn(str(retained[0]), result.stderr)
                self.assertTrue((retained[0] / "publish/libe_sqlite3.dylib").exists())
                if stage == "publish":
                    self.assertFalse(any(call[:2] == ["hdiutil", "create"] for call in fixture.calls()))
                else:
                    self.assertTrue((retained[0] / "SZU-Badminton-Draw_5.7.9_osx-arm64.dmg").exists())

    def test_ci_version_command_evaluates_project_without_creating_artifacts(self):
        with Fixture() as fixture:
            result = subprocess.run(
                [shutil.which("python3"), str(fixture.repo / "scripts/packaging_metadata.py"), "version",
                 str(fixture.repo / "src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj"), "Release"],
                env=fixture.env, capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout, "5.7.9\n")
            self.assertFalse((fixture.repo / "artifacts").exists())

    def output_paths(self, result):
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        app = next(line.removeprefix("Created app bundle: ") for line in result.stdout.splitlines()
                   if line.startswith("Created app bundle: "))
        dmg = next(line.removeprefix("Created DMG: ") for line in result.stdout.splitlines()
                   if line.startswith("Created DMG: "))
        self.assertTrue(Path(app).is_absolute())
        self.assertTrue(Path(dmg).is_absolute())
        return Path(app), Path(dmg)

    def test_repeated_valid_runs_keep_previous_bundle_dmg_and_evidence(self):
        # Removing fresh-run allocation would replace the first DMG/app or delete evidence.
        with Fixture() as fixture:
            first_app, first_dmg = self.output_paths(fixture.run("osx-arm64"))
            first_bytes = first_dmg.read_bytes()
            sentinel = first_dmg.parent / "operator-evidence.txt"
            sentinel.write_text("must survive the next run")
            second_app, second_dmg = self.output_paths(fixture.run("osx-arm64"))
            self.assertNotEqual(first_dmg, second_dmg)
            self.assertNotEqual(first_app, second_app)
            self.assertEqual(first_dmg.read_bytes(), first_bytes)
            self.assertEqual(sentinel.read_text(), "must survive the next run")
            for app, dmg in ((first_app, first_dmg), (second_app, second_dmg)):
                self.assert_bundle(fixture, app, dmg, "5.7.9", False, "SZU Badminton Draw")
            publishes = [call for call in fixture.calls() if call[:2] == ["dotnet", "publish"]]
            self.assertEqual(len(publishes), 2)
            for call in publishes:
                self.assertIn("-p:Version=5.7.9", call)
                self.assertIn("-p:VersionPrefix=5.7.9", call)
            creates = [call for call in fixture.calls() if call[:2] == ["hdiutil", "create"]]
            self.assertEqual(len(creates), 2)
            self.assertTrue(all("-ov" not in call for call in creates))
            self.assertEqual(len([call for call in fixture.calls() if call[:2] == ["hdiutil", "verify"]]), 2)

    def assert_bundle(self, fixture, app, dmg, version, dirty, name, rid="osx-arm64"):
        self.assertEqual(dmg.name, f"SZU-Badminton-Draw_{version}_{rid}.dmg")
        self.assertEqual(dmg.parent.parent.name, version)
        with (app / "Contents/Info.plist").open("rb") as stream:
            info = plistlib.load(stream)
        self.assertEqual(info["CFBundleShortVersionString"], version)
        self.assertEqual(info["CFBundleVersion"], version)
        self.assertEqual(info["CFBundleDisplayName"], name)
        self.assertEqual(info["CFBundleIdentifier"], "com.szuba.badmintondraw")
        self.assertEqual(info["CFBundleIconFile"], "AppIcon")
        metadata = json.loads((app / "Contents/Resources/build-metadata.json").read_text())
        self.assertEqual(metadata["version"], version)
        self.assertEqual(metadata["sourceCommit"], fixture.commit)
        self.assertIs(metadata["sourceDirty"], dirty)
        self.assertEqual(metadata["runtimeIdentifier"], rid)
        self.assertTrue(os.access(app / "Contents/MacOS/BadmintonDraw.Desktop", os.X_OK))
        self.assertEqual((app / "Contents/MacOS/libe_sqlite3.dylib").read_bytes(), b"MOCK NATIVE ASSET\n")
        self.assertEqual(os.readlink(app.parent / "Applications"), "/Applications")

    def test_override_version_dirty_source_and_xml_name_match_real_outputs(self):
        with Fixture() as fixture:
            (fixture.repo / "Directory.Build.props").write_text("<!-- dirty source -->\n")
            name = "深大 & <Final>"
            app, dmg = self.output_paths(fixture.run("osx-x64", VERSION="6.2.3", APP_NAME=name))
            self.assert_bundle(fixture, app, dmg, "6.2.3", True, name, "osx-x64")
            publishes = [call for call in fixture.calls() if call[:2] == ["dotnet", "publish"]]
            self.assertEqual(len(publishes), 1)
            self.assertIn("-p:Version=6.2.3", publishes[0])
            self.assertIn("-p:VersionPrefix=6.2.3", publishes[0])
            self.assertEqual(publishes[0][publishes[0].index("-r") + 1], "osx-x64")
            self.assertEqual(publishes[0][publishes[0].index("--self-contained") + 1], "true")
            self.assertFalse(any(call[:2] == ["dotnet", "msbuild"] for call in fixture.calls()))


if __name__ == "__main__":
    unittest.main(verbosity=2)
