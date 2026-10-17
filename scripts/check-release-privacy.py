#!/usr/bin/env python3
"""Check an app directory or ZIP for a small set of release privacy hazards.

Uses only the Python standard library. This is a targeted release check, not a
complete secret scanner. Findings contain relative names (or anonymous labels),
byte offsets and categories only; matched bytes and exceptions are never printed.

Usage: python3 scripts/check-release-privacy.py '/path/Example.app'
Exit status: 0 = passed; 1 = findings; 2 = input/read error.
"""

import argparse
import fnmatch
import os
import re
import stat
import sys
import zipfile
import zlib
from pathlib import Path, PurePosixPath


EMAIL_ADDRESS = (
    rb"(?<![A-Z0-9._%+\-])[A-Z0-9._%+\-]+@"
    rb"(?:qq\.com|163\.com|126\.com|gmail\.com|outlook\.com|hotmail\.com)"
)
CONTENT_RULES = (
    (
        "home-path",
        re.compile(
            rb"/(?:Users|home)/[A-Za-z0-9_.\-\x80-\xff]"
            rb"[A-Za-z0-9_. \-\x80-\xff]*(?:/|(?=[\x00-\x20\"'<>]|$))"
        ),
    ),
    (
        "personal-email",
        re.compile(
            EMAIL_ADDRESS + rb"(?![A-Z0-9.\-])",
            re.IGNORECASE,
        ),
    ),
    (
        "github-token-candidate",
        re.compile(
            rb"(?<![A-Za-z0-9_])(?:gh[pousr]_[A-Za-z0-9]{20,}"
            rb"|github_pat_[A-Za-z0-9_]{40,})(?![A-Za-z0-9_])"
        ),
    ),
    (
        "api-key-candidate",
        re.compile(
            rb"(?<![A-Za-z0-9_\-])(?:sk-[A-Za-z0-9_\-]{24,}"
            rb"|sk_live_[A-Za-z0-9]{24,}|AIza[A-Za-z0-9_\-]{30,}"
            rb"|xox[baprs]-[A-Za-z0-9\-]{24,})(?![A-Za-z0-9_\-])"
        ),
    ),
    (
        "api-key-candidate",
        re.compile(
            rb"(?:api[_\-]?key|api[_\-]?token|access[_\-]?token"
            rb"|refresh[_\-]?token|client[_\-]?secret)[\"']?\s*[:=]\s*"
            rb"[\"'][A-Za-z0-9_./+=\-]{32,}[\"']",
            re.IGNORECASE,
        ),
    ),
    (
        "jwt-candidate",
        re.compile(
            rb"(?<![A-Za-z0-9_\-])eyJ[A-Za-z0-9_\-]{5,}\."
            rb"[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}"
            rb"(?![A-Za-z0-9_\-])"
        ),
    ),
    (
        "private-key-candidate",
        re.compile(
            rb"-----BEGIN (?:(?:RSA|EC|DSA|OPENSSH|ENCRYPTED) )?PRIVATE KEY-----"
            rb"|-----BEGIN PGP PRIVATE KEY BLOCK-----"
        ),
    ),
)
# In a filename, a dot may introduce an extension after the email address.
NAME_RULES = (("personal-email", re.compile(EMAIL_ADDRESS + rb"(?![A-Z0-9\-])", re.IGNORECASE)),) + CONTENT_RULES

RUNTIME_DIRECTORIES = frozenset(("accounts", "dev-auth-store", "auth-backups"))
RUNTIME_FILES = frozenset(
    (
        "auth.json",
        "cache.json",
        "config.json",
        "usage-external-auth.json",
        "credentials.json",
        "tokens.json",
        ".env",
    )
)


class Scanner:
    def __init__(self):
        self.findings = 0
        self.errors = 0
        self.files = 0
        self.entries = 0

    def report(self, name, category, offset=None, error=False):
        # Escape control characters so an untrusted archive name cannot inject
        # additional output lines. Never include a matched value or exception.
        if any(pattern.search(os.fsencode(name)) for _, pattern in NAME_RULES):
            name = "<redacted-entry>"
        display_name = str(name).encode("unicode_escape").decode("ascii")
        position = " byte={}".format(offset) if offset is not None else ""
        print("{} {}{} category={}".format("ERROR" if error else "FAIL", display_name, position, category))
        if error:
            self.errors += 1
        else:
            self.findings += 1

    def scan_name(self, name, label=None):
        self.entries += 1
        label = label or "<entry:{}>".format(self.entries)
        reported = set()
        for category, pattern in NAME_RULES:
            match = pattern.search(os.fsencode(name))
            if match and category not in reported:
                self.report(label, "filename-" + category, match.start())
                reported.add(category)
        return label if reported else name

    def scan_bytes(self, name, data):
        self.files += 1
        reported = set()
        for category, pattern in CONTENT_RULES:
            if category in reported:
                continue
            match = pattern.search(data)
            if match:
                self.report(name, category, match.start())
                reported.add(category)

    @staticmethod
    def runtime_path(name, is_directory=False):
        parts = PurePosixPath(str(name).replace("\\", "/")).parts
        directory_parts = parts if is_directory else parts[:-1]
        if any(part.casefold() in RUNTIME_DIRECTORIES for part in directory_parts):
            return True
        if is_directory or not parts:
            return False
        filename = parts[-1].casefold()
        return filename in RUNTIME_FILES or fnmatch.fnmatchcase(filename, "auth-*.json")

    def scan_directory(self, root):
        root = root.resolve()

        def walk_error(exception):
            # os.walk's exception may contain the absolute input path.
            location = Path(exception.filename) if exception.filename else root
            try:
                name = location.relative_to(root).as_posix() or "."
            except ValueError:
                name = "<input>"
            self.report(self.scan_name(name), "read-error", error=True)

        for current, directories, filenames in os.walk(root, onerror=walk_error, followlinks=False):
            current = Path(current)
            for directory in sorted(directories):
                path = current / directory
                name = path.relative_to(root).as_posix()
                display_name = self.scan_name(name)
                if self.runtime_path(name, is_directory=True):
                    self.report(display_name + "/", "runtime-credentials")
                    directories.remove(directory)
                elif path.is_symlink():
                    self.scan_symlink(path, display_name, root)
                    directories.remove(directory)
            for filename in sorted(filenames):
                path = current / filename
                name = path.relative_to(root).as_posix()
                display_name = self.scan_name(name)
                if self.runtime_path(name):
                    self.report(display_name, "runtime-credentials")
                    continue
                if path.is_symlink():
                    self.scan_symlink(path, display_name, root)
                    continue
                try:
                    self.scan_bytes(display_name, path.read_bytes())
                except OSError:
                    self.report(display_name, "read-error", error=True)

    def scan_symlink(self, path, name, root):
        # Read the link itself, never files outside the supplied bundle.
        try:
            target = os.readlink(path)
            self.scan_bytes(name, os.fsencode(target))
            if os.path.isabs(target):
                self.report(name, "external-symlink")
            elif ".." in PurePosixPath(target).parts:
                resolved = path.resolve()
                try:
                    resolved.relative_to(root)
                except ValueError:
                    self.report(name, "external-symlink")
        except (OSError, RuntimeError):
            self.report(name, "read-error", error=True)

    @staticmethod
    def external_zip_symlink(name, data):
        target = os.fsdecode(data).replace("\\", "/")
        if target.startswith("/") or re.match(r"^[A-Za-z]:", target) or "\x00" in target:
            return True
        parts = list(PurePosixPath(name.replace("\\", "/")).parent.parts)
        # A ZIP may wrap its bundle in another directory; links must stay
        # inside that app, or inside the archive if it contains no app folder.
        minimum_depth = next((i + 1 for i, part in enumerate(parts) if part.casefold().endswith(".app")), 0)
        for part in target.split("/"):
            if part == "..":
                if len(parts) <= minimum_depth:
                    return True
                parts.pop()
            elif part not in ("", "."):
                parts.append(part)
        return False

    @staticmethod
    def unsafe_zip_name(name):
        normalized = name.replace("\\", "/")
        return (
            normalized.startswith("/")
            or re.match(r"^[A-Za-z]:", normalized) is not None
            or ".." in PurePosixPath(normalized).parts
            or any(ord(character) < 32 for character in normalized)
        )

    def scan_zip(self, path):
        try:
            with zipfile.ZipFile(path) as archive:
                for index, member in enumerate(archive.infolist()):
                    name = member.filename
                    display_name = self.scan_name(name, "<zip-entry:{}>".format(index))
                    if self.unsafe_zip_name(name):
                        # An unsafe member could itself contain a personal
                        # absolute path; report its index instead of its name.
                        self.report("<zip-entry:{}>".format(index), "unsafe-zip-entry")
                        continue
                    if self.runtime_path(name, is_directory=member.is_dir()):
                        self.report(display_name, "runtime-credentials")
                        continue
                    is_symlink = stat.S_ISLNK(member.external_attr >> 16)
                    if member.is_dir() and not is_symlink:
                        continue
                    try:
                        data = archive.read(member)
                        self.scan_bytes(display_name, data)
                        if is_symlink and self.external_zip_symlink(name, data):
                            self.report(display_name, "external-symlink")
                    except (OSError, RuntimeError, ValueError, EOFError, zipfile.BadZipFile, NotImplementedError, zlib.error):
                        self.report(display_name, "read-error", error=True)
        except (OSError, RuntimeError, ValueError, EOFError, zipfile.BadZipFile, zipfile.LargeZipFile, NotImplementedError):
            self.report("<input>", "read-error", error=True)

    def result(self):
        if self.errors:
            print("ERROR: {} read error(s); {} finding(s).".format(self.errors, self.findings))
            return 2
        if self.findings:
            print("FAIL: {} finding(s) in {} scanned file(s).".format(self.findings, self.files))
            return 1
        print("PASS: {} file(s) checked; no configured privacy patterns found.".format(self.files))
        return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("target", help="App directory or ZIP archive to check")
    arguments = parser.parse_args()
    scanner = Scanner()
    target = Path(arguments.target)
    scanner.scan_name(target.name, "<input>")
    try:
        if target.is_symlink():
            # The root should name the artifact itself, not a link elsewhere.
            scanner.report("<input>", "read-error", error=True)
        elif target.is_dir():
            scanner.scan_directory(target)
        elif target.is_file():
            scanner.scan_zip(target)
        else:
            scanner.report("<input>", "read-error", error=True)
    except OSError:
        scanner.report("<input>", "read-error", error=True)
    return scanner.result()


if __name__ == "__main__":
    sys.exit(main())
