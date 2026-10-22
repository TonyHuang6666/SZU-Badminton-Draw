#!/usr/bin/env python3
"""Tests for the publish guard; fixture files are not release artifacts."""
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

from check_help_publish import check_publish


class HelpPublishTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="help publish test ")
        self.root = Path(self.temp.name)
        (self.root / "BadmintonDraw.Desktop").write_bytes(b"fixture executable")
        self.deps = self.root / "BadmintonDraw.Desktop.deps.json"
        self.deps.write_text(json.dumps({"libraries": {"BadmintonDraw.Desktop/5.0.2": {}}}))

    def tearDown(self):
        self.temp.cleanup()

    def test_clean_runtime_is_accepted(self):
        check_publish(self.root)

    def test_old_notice_in_reused_output_is_rejected(self):
        notices = self.root / "ThirdPartyNotices"
        notices.mkdir()
        (notices / "Markdig.txt").write_text("old notice")
        with self.assertRaisesRegex(ValueError, "Markdig.txt"):
            check_publish(self.root)

    def test_dependency_reference_is_rejected_even_without_the_dll(self):
        self.deps.write_text(json.dumps({"libraries": {"Markdig/1.4.0": {}}}))
        with self.assertRaisesRegex(ValueError, "Markdig/1.4.0"):
            check_publish(self.root)

    def test_single_file_manifest_is_checked(self):
        self.deps.unlink()
        bundle = self.root / "BadmintonDraw.Desktop.exe"
        names = b"BadmintonDraw.Desktop.dll\0BadmintonDraw.Desktop.deps.json\0"
        bundle.write_bytes(names)
        check_publish(self.root)
        bundle.write_bytes(names + b"Markdig.dll\0")
        with self.assertRaisesRegex(ValueError, "Markdig.dll"):
            check_publish(self.root)

    def test_build_tool_cannot_be_included(self):
        (self.root / "BadmintonDraw.HelpCompiler.dll").write_bytes(b"fixture")
        with self.assertRaisesRegex(ValueError, "HelpCompiler"):
            check_publish(self.root)

    def test_empty_output_is_not_a_verified_publish(self):
        self.deps.unlink()
        with self.assertRaisesRegex(ValueError, "dependency manifest"):
            check_publish(self.root)

    def test_msbuild_rejects_stale_files_in_the_actual_publish_directory(self):
        repository = Path(__file__).resolve().parents[1]
        command = ["dotnet", "msbuild", "src/BadmintonDraw.Desktop/BadmintonDraw.Desktop.csproj",
                   "-t:CheckHelpPublishedDirectory", f"-p:PublishDir={self.root}/", "-v:quiet", "-nologo"]
        clean = subprocess.run(command, cwd=repository, text=True, capture_output=True, timeout=60)
        self.assertEqual(0, clean.returncode, clean.stdout + clean.stderr)
        for relative in ("ThirdPartyNotices/Markdig.txt", "Markdig.dll", "BadmintonDraw.HelpCompiler.dll"):
            with self.subTest(relative=relative):
                stale = self.root / relative
                stale.parent.mkdir(parents=True, exist_ok=True)
                stale.write_text("stale fixture")
                rejected = subprocess.run(command, cwd=repository, text=True, capture_output=True, timeout=60)
                self.assertNotEqual(0, rejected.returncode)
                self.assertIn("Build-only help files remain", rejected.stdout + rejected.stderr)
                self.assertTrue(stale.exists(), "The guard must report, not delete existing output.")
                stale.unlink()


if __name__ == "__main__":
    unittest.main()
