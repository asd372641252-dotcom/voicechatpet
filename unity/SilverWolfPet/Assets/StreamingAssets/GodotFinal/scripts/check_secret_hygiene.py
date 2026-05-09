from __future__ import annotations

import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

TEXT_EXTENSIONS = {
    ".cs",
    ".csproj",
    ".editorconfig",
    ".gd",
    ".gdshader",
    ".gitattributes",
    ".gitignore",
    ".json",
    ".md",
    ".ps1",
    ".py",
    ".shader",
    ".ts",
    ".tsx",
    ".txt",
    ".yaml",
    ".yml",
}

SKIP_PARTS = {
    ".git",
    ".godot",
    ".tmp",
    ".venv",
    ".vs",
    "__pycache__",
    "bin",
    "generated",
    "logs",
    "node_modules",
    "obj",
    "snapshots",
}

SECRET_PATTERNS = (
    ("openai_compatible_key", re.compile(r"\b(?:sk|tp)-[A-Za-z0-9_-]{20,}\b")),
    ("volc_access_key_marker", re.compile(r"\bAKLT[A-Za-z0-9_-]{12,}\b")),
    ("volc_secret_marker", re.compile(r"\bWVRJ[A-Za-z0-9+/=_-]{20,}\b")),
    ("long_bearer_token", re.compile(r"\bBearer\s+[A-Za-z0-9._-]{24,}\b")),
)

SECRET_JSON_FIELD = re.compile(
    r'"(?P<key>AccessToken|Token|ApiKey|APIKey|api_key|apikey|SecretAccessKey|AccessKeyId|Authorization)"\s*:\s*"(?P<value>[^"]{8,})"'
)

SAFE_VALUE_PREFIXES = (
    "$",
    "${",
    "<",
    "your",
    "YOUR",
    "example",
    "EXAMPLE",
)

SAFE_EXACT_VALUES = {
    "Bearer <token>",
    "Bearer ${TOKEN}",
}


@dataclass(frozen=True)
class Finding:
    path: Path
    line: int
    code: str
    snippet: str


def main() -> int:
    findings = scan(ROOT)
    if findings:
        for finding in findings:
            rel = finding.path.relative_to(ROOT)
            print(f"{rel}:{finding.line}: {finding.code}: {finding.snippet}")
        print(f"\nSecret hygiene check failed: {len(findings)} finding(s).")
        return 1
    print("Secret hygiene check passed.")
    return 0


def scan(root: Path) -> list[Finding]:
    findings: list[Finding] = []
    for path in iter_candidate_files(root):
        try:
            text = path.read_text(encoding="utf-8-sig")
        except UnicodeDecodeError:
            continue
        for line_no, line in enumerate(text.splitlines(), start=1):
            findings.extend(check_line(path, line_no, line))
    return findings


def iter_candidate_files(root: Path) -> list[Path]:
    result = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard"],
        cwd=root,
        text=True,
        encoding="utf-8",
        errors="replace",
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if result.returncode != 0:
        return iter_filesystem_candidate_files(root)

    files: list[Path] = []
    for raw in result.stdout.splitlines():
        rel = raw.strip()
        if not rel:
            continue
        path = root / rel
        if not path.is_file():
            continue
        if should_skip(path, root):
            continue
        files.append(path)
    return sorted(files)


def iter_filesystem_candidate_files(root: Path) -> list[Path]:
    files: list[Path] = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        if should_skip(path, root):
            continue
        files.append(path)
    return sorted(files)


def should_skip(path: Path, root: Path) -> bool:
    try:
        rel = path.relative_to(root)
    except ValueError:
        return True
    if any(part in SKIP_PARTS for part in rel.parts):
        return True
    suffix = path.suffix.lower()
    if suffix in TEXT_EXTENSIONS:
        return False
    return path.name in TEXT_EXTENSIONS


def check_line(path: Path, line_no: int, line: str) -> list[Finding]:
    findings: list[Finding] = []
    stripped = line.strip()
    if not stripped:
        return findings

    for code, pattern in SECRET_PATTERNS:
        if pattern.search(line):
            findings.append(Finding(path, line_no, code, redact(line)))

    for match in SECRET_JSON_FIELD.finditer(line):
        value = match.group("value").strip()
        if is_safe_placeholder(value):
            continue
        findings.append(Finding(path, line_no, "literal_secret_json_field", redact(line)))

    return findings


def is_safe_placeholder(value: str) -> bool:
    if value in SAFE_EXACT_VALUES:
        return True
    if value.startswith(SAFE_VALUE_PREFIXES):
        return True
    if value.lower() in {"null", "none", "changeme", "replace_me"}:
        return True
    return False


def redact(text: str) -> str:
    text = SECRET_JSON_FIELD.sub(lambda m: f'"{m.group("key")}": "<redacted>"', text)
    text = re.sub(r"\b(?:sk|tp)-[A-Za-z0-9_-]{8,}\b", "<redacted-api-key>", text)
    text = re.sub(r"\bAKLT[A-Za-z0-9_-]{8,}\b", "<redacted-access-key>", text)
    text = re.sub(r"\bWVRJ[A-Za-z0-9+/=_-]{8,}\b", "<redacted-secret>", text)
    text = re.sub(r"\bBearer\s+[A-Za-z0-9._-]{8,}\b", "Bearer <redacted>", text)
    return text[:240]


if __name__ == "__main__":
    raise SystemExit(main())
