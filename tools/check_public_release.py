"""Publication guard: inspect tracked files without printing matched content.

This supplements a secret scanner and human review; it is not a malware scan.
Run only in the public source checkout, never over a game installation.
"""
import argparse
import ipaddress
import re
import subprocess
from pathlib import Path, PurePosixPath


ROOT_FILES = {"README.md", "CHANGELOG.md", "LICENSE", "NOTICE.md", "SECURITY.md", ".gitignore", "ODDBA-发布文案.txt"}
FOLDERS = {"src", "tests", "tests-config", "tests-native", "tools", "docs", ".github"}
EXTENSIONS = {".cs", ".csproj", ".py", ".md", ".txt", ".tsv", ".yml", ".yaml"}
FORBIDDEN_PARTS = {"research", "backup", "backups", "package", "verification", "bin", "obj", "__pycache__", ".codex"}
PATTERNS = {
    "local-path": re.compile(r"(?i)(?:\b[a-z]:[\\/]|/mnt/[a-z]/|/(?:home|Users)/[^\s/]+/|\\\\(?:\d{1,3}\.){3}\d{1,3})"),
    "private-key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    "token": re.compile(r"(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9]{24,})"),
}
CONVERSATION = re.compile(r"\u672c\u8f6e|\u52a9\u624b|\u6309.{0,12}\u7528\u6237|\u7528\u6237.{0,20}\u5b9e\u6d4b|\u4f60\u6b63\u5728|\u4f60\u5f53\u524d|\u5bf9\u8bdd\u4e0a\u4e0b\u6587|<\|(?:system|user|assistant)\|>")


def inspect(name, data):
    path = PurePosixPath(name)
    findings = []
    if (path.is_absolute() or ".." in path.parts or any(p.lower() in FORBIDDEN_PARTS for p in path.parts)
            or (name not in ROOT_FILES and (path.parts[0] not in FOLDERS or path.suffix.lower() not in EXTENSIONS))):
        findings.append("unexpected-file")
    try:
        source = data.decode("utf-8")
    except UnicodeDecodeError:
        return findings + ["non-text-file"]
    if "\x00" in source:
        findings.append("binary-content")
    for label, pattern in PATTERNS.items():
        if pattern.search(source):
            findings.append(label)
    if path.suffix in {".md", ".txt"} and name != "LICENSE" and CONVERSATION.search(source):
        findings.append("conversation-prose")
    for address in re.findall(r"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])", source):
        try:
            candidate = ipaddress.IPv4Address(address)
        except ipaddress.AddressValueError:
            continue
        if candidate.is_private or candidate.is_loopback or candidate.is_link_local:
            findings.append("local-network-address")
            break
    return findings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    args = parser.parse_args()
    root = args.root.resolve()
    tracked = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode().split("\0")
    failures = []
    checked = 0
    for name in filter(None, tracked):
        path = root / name
        if path.is_symlink() or not path.is_file():
            failures.append((name, "not-regular-file"))
            continue
        checked += 1
        failures.extend((name, reason) for reason in inspect(name, path.read_bytes()))
    if checked == 0:
        failures.append(("repository", "no-tracked-files"))
    # Only public noreply identities are permitted in published history.
    history = subprocess.run(["git", "log", "--all", "--format=%ae%n%ce"], cwd=root, capture_output=True, text=True)
    if history.returncode == 0:
        if any(not email.endswith("@users.noreply.github.com") for email in history.stdout.splitlines()):
            failures.append(("git-history", "non-noreply-email"))
        messages = subprocess.check_output(["git", "log", "--all", "--format=%B"], cwd=root).decode()
        if CONVERSATION.search(messages) or any(p.search(messages) for p in PATTERNS.values()):
            failures.append(("git-history", "private-commit-message"))
        # Deleting a file from HEAD does not remove it from published history.
        objects = subprocess.check_output(["git", "rev-list", "--objects", "--all"], cwd=root).decode().splitlines()
        object_names = dict(row.split(" ", 1) for row in objects if " " in row)
        kinds = subprocess.run(["git", "cat-file", "--batch-check=%(objectname) %(objecttype)"],
                               cwd=root, input="".join(oid + "\n" for oid in object_names), capture_output=True, text=True, check=True)
        for line in kinds.stdout.splitlines():
            oid, kind = line.split()
            if kind != "blob":
                continue
            data = subprocess.check_output(["git", "cat-file", "blob", oid], cwd=root)
            failures.extend(("history/" + object_names[oid], reason) for reason in inspect(object_names[oid], data))
    for name, reason in failures:
        print(f"FAIL {name}: {reason}")
    if failures:
        raise SystemExit(1)
    print(f"PASS: {checked} tracked files satisfy the publication allowlist and privacy rules.")


if __name__ == "__main__":
    main()
