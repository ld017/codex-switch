"""Regression tests use synthetic paths and credentials only."""

import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "check-release-privacy.py"


class ReleasePrivacyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.app = self.root / "Example.app"
        self.app.mkdir()

    def run_check(self, target=None):
        return subprocess.run(
            [sys.executable, str(SCRIPT), str(target or self.app)],
            capture_output=True,
            text=True,
            check=False,
        )

    def add_file(self, relative, content=b""):
        destination = self.app / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(content)

    def test_rejects_home_paths_without_printing_the_match(self):
        for private_path in (
            b"/Users/example-user/private-project/main.swift",
            b"/home/example-user/private-project/main.swift",
            b"/Users/example-user\x00",
            b"/home/example-user",
            b"/Users/example user\x00",
            b"/Users/example user/private-project/main.swift",
        ):
            with self.subTest(private_path=private_path):
                self.add_file("Contents/MacOS/Example", b"\x00binary\x00" + private_path)
                result = self.run_check()
                self.assertEqual(result.returncode, 1)
                self.assertIn("Contents/MacOS/Example", result.stdout)
                self.assertIn("byte=8", result.stdout)
                self.assertIn("category=home-path", result.stdout)
                self.assertNotIn(private_path.decode(), result.stdout + result.stderr)
                self.assertNotIn("example-user", result.stdout + result.stderr)
                self.assertNotIn("example user", result.stdout + result.stderr)

    def test_rejects_runtime_auth_files(self):
        for name in ("auth.json", "auth-personal.json", "cache.json", "config.json"):
            with self.subTest(name=name):
                self.add_file("Contents/Resources/" + name, b"{}")
                result = self.run_check()
                self.assertEqual(result.returncode, 1)
                self.assertIn("category=runtime-credentials", result.stdout)
                (self.app / "Contents/Resources" / name).unlink()

    def test_rejects_empty_runtime_directories(self):
        for name in ("accounts", "dev-auth-store", "auth-backups"):
            with self.subTest(name=name):
                directory = self.app / "Contents/Resources" / name
                directory.mkdir(parents=True)
                result = self.run_check()
                self.assertEqual(result.returncode, 1)
                self.assertIn("category=runtime-credentials", result.stdout)
                directory.rmdir()

    def test_allows_clean_bundle_identifiers_and_placeholder_paths(self):
        self.add_file(
            "Contents/Info.plist",
            b"com.example.switch\ncom.example.switch.helper\n/Users/<username>/project\n",
        )
        result = self.run_check()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("PASS", result.stdout)

    def test_rejects_personal_email_without_printing_it(self):
        for domain in ("qq.com", "163.com", "126.com", "gmail.com", "outlook.com", "hotmail.com"):
            with self.subTest(domain=domain):
                address = "example-person@" + domain
                self.add_file("Contents/Resources/contact.txt", address.encode())
                result = self.run_check()
                self.assertEqual(result.returncode, 1)
                self.assertIn("category=personal-email", result.stdout)
                self.assertNotIn(address, result.stdout + result.stderr)

    def test_rejects_obvious_secret_candidates_without_printing_them(self):
        samples = (
            ("api-key-candidate", "sk-" + "SyntheticTestValue0123456789" * 2),
            ("api-key-candidate", 'api_key = "' + "SyntheticTestValue0123456789" * 2 + '"'),
            ("github-token-candidate", "ghp_" + "SyntheticTestValue0123456789" * 2),
            ("github-token-candidate", "github_pat_" + "SyntheticTestValue0123456789" * 3),
            ("jwt-candidate", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJleGFtcGxlIn0.c3ludGhldGljU2lnbmF0dXJl"),
            ("private-key-candidate", "-----BEGIN OPENSSH PRIVATE KEY-----"),
        )
        for category, value in samples:
            with self.subTest(category=category):
                self.add_file("Contents/Resources/data.bin", value.encode())
                result = self.run_check()
                self.assertEqual(result.returncode, 1)
                self.assertIn("category=" + category, result.stdout)
                self.assertNotIn(value, result.stdout + result.stderr)

    def test_scans_zip_member_bytes_without_extracting(self):
        archive = self.root / "release.zip"
        with zipfile.ZipFile(archive, "w") as bundle:
            bundle.writestr("Example.app/Contents/MacOS/Example", b"/Users/example-user/main.swift")
        result = self.run_check(archive)
        self.assertEqual(result.returncode, 1)
        self.assertIn("Example.app/Contents/MacOS/Example", result.stdout)
        self.assertIn("category=home-path", result.stdout)
        self.assertFalse((self.root / "Example.app/Contents").exists())

    def test_rejects_unsafe_zip_entries(self):
        for member in ("../outside.txt", "/absolute.txt", "C:/absolute.txt", "folder\\..\\outside.txt"):
            with self.subTest(member=member):
                archive = self.root / "unsafe.zip"
                with zipfile.ZipFile(archive, "w") as bundle:
                    bundle.writestr(member, b"safe content")
                result = self.run_check(archive)
                self.assertEqual(result.returncode, 1)
                self.assertIn("category=unsafe-zip-entry", result.stdout)
                self.assertFalse((self.root / "outside.txt").exists())

    def test_rejects_runtime_files_in_zip(self):
        archive = self.root / "auth.zip"
        with zipfile.ZipFile(archive, "w") as bundle:
            bundle.writestr("Example.app/Contents/Resources/accounts/", b"")
            bundle.writestr("Example.app/Contents/Resources/auth.json", b"{}")
        result = self.run_check(archive)
        self.assertEqual(result.returncode, 1)
        self.assertIn("category=runtime-credentials", result.stdout)

    def test_allows_clean_zip(self):
        archive = self.root / "clean.zip"
        with zipfile.ZipFile(archive, "w") as bundle:
            bundle.writestr("Example.app/Contents/Info.plist", b"com.example.switch")
        result = self.run_check(archive)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_rejects_private_file_and_directory_names_without_echo(self):
        names = (
            ("example-person@gmail.com.txt", "personal-email"),
            ("ghp_" + "SyntheticTestValue0123456789" * 2, "github-token-candidate"),
        )
        for name, category in names:
            with self.subTest(category=category):
                self.add_file("Contents/Resources/" + name, b"/home/example-user")
                result = self.run_check()
                self.assertEqual(result.returncode, 1)
                self.assertIn("category=filename-" + category, result.stdout)
                self.assertIn("<entry:", result.stdout)
                self.assertNotIn(name, result.stdout + result.stderr)
                self.assertNotIn("example-person", result.stdout + result.stderr)
                (self.app / "Contents/Resources" / name).unlink()
        directory = self.app / "Contents/Resources/example-person@gmail.com"
        directory.mkdir()
        result = self.run_check()
        self.assertEqual(result.returncode, 1)
        self.assertNotIn(directory.name, result.stdout + result.stderr)

    def test_rejects_private_zip_names_without_echo(self):
        archive = self.root / "private-names.zip"
        name = "auth-example-person@gmail.com.json"
        with zipfile.ZipFile(archive, "w") as bundle:
            bundle.writestr("Example.app/Contents/Resources/" + name, b"{}")
        result = self.run_check(archive)
        self.assertEqual(result.returncode, 1)
        self.assertIn("category=filename-personal-email", result.stdout)
        self.assertIn("category=runtime-credentials", result.stdout)
        self.assertIn("<zip-entry:0>", result.stdout)
        self.assertNotIn(name, result.stdout + result.stderr)
        self.assertNotIn("example-person", result.stdout + result.stderr)

    def test_rejects_private_artifact_basename_without_echo(self):
        archive = self.root / "example-person@gmail.com.zip"
        with zipfile.ZipFile(archive, "w") as bundle:
            bundle.writestr("Example.app/Contents/Info.plist", b"com.example.switch")
        result = self.run_check(archive)
        self.assertEqual(result.returncode, 1)
        self.assertIn("<input>", result.stdout)
        self.assertNotIn(archive.name, result.stdout + result.stderr)

    def test_zip_symlinks_reject_absolute_and_escaping_targets(self):
        for target in ("/tmp/example-outside", "../../../outside", "../../../../outside", "C:\\outside"):
            with self.subTest(target=target):
                archive = self.root / "symlink.zip"
                with zipfile.ZipFile(archive, "w") as bundle:
                    for suffix in ("link", "link/"):
                        member = zipfile.ZipInfo("Example.app/Contents/Resources/" + suffix)
                        member.create_system = 3
                        member.external_attr = (stat.S_IFLNK | 0o777) << 16
                        bundle.writestr(member, target.encode())
                result = self.run_check(archive)
                self.assertEqual(result.returncode, 1)
                self.assertEqual(result.stdout.count("category=external-symlink"), 2)
                self.assertIn("category=external-symlink", result.stdout)
                self.assertNotIn(target, result.stdout + result.stderr)
                self.assertFalse((self.root / "outside").exists())

    def test_zip_symlinks_allow_relative_targets_inside_bundle(self):
        for target in ("value.txt", "../shared/value.txt"):
            with self.subTest(target=target):
                archive = self.root / "internal-symlink.zip"
                member = zipfile.ZipInfo("Example.app/Contents/Resources/link")
                member.create_system = 3
                member.external_attr = (stat.S_IFLNK | 0o777) << 16
                with zipfile.ZipFile(archive, "w") as bundle:
                    bundle.writestr(member, target.encode())
                result = self.run_check(archive)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_allows_internal_symlink_but_rejects_external_symlink(self):
        self.add_file("Contents/Resources/value.txt", b"safe content")
        link = self.app / "Contents/Resources/link.txt"
        link.symlink_to("value.txt")
        result = self.run_check()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        link.unlink()
        link.symlink_to("../../../outside.txt")
        result = self.run_check()
        self.assertEqual(result.returncode, 1)
        self.assertIn("category=external-symlink", result.stdout)
        link.unlink()
        link.symlink_to(self.app / "Contents/Resources/value.txt")
        result = self.run_check()
        self.assertEqual(result.returncode, 1)
        self.assertIn("category=external-symlink", result.stdout)

    def test_read_errors_return_nonzero_without_exposing_absolute_input(self):
        for target in (self.root / "missing.app", self.root / "invalid.zip"):
            with self.subTest(target=target.name):
                if target.suffix == ".zip":
                    target.write_bytes(b"not a zip")
                result = self.run_check(target)
                self.assertEqual(result.returncode, 2)
                self.assertIn("category=read-error", result.stdout + result.stderr)
                self.assertNotIn(str(target), result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
