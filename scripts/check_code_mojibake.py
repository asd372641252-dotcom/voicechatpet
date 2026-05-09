from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

DEFAULT_ROOTS = (
    "scripts",
    "src",
    "tools",
    "tests",
    "config",
    "scenes",
    "assets/PetDesktop",
)
TEXT_EXTENSIONS = {
    ".py",
    ".gd",
    ".cs",
    ".ts",
    ".tsx",
    ".js",
    ".json",
    ".md",
    ".yaml",
    ".yml",
    ".txt",
    ".ps1",
    ".tscn",
    ".tres",
    ".shader",
    ".gdshader",
    ".csproj",
}
SKIP_DIRS = {
    ".git",
    ".godot",
    ".tmp",
    ".venv",
    ".vs",
    "__pycache__",
    "backups",
    "bin",
    "generated",
    "head_tracker",
    "Library",
    "logs",
    "node_modules",
    "obj",
    "site-packages",
    "snapshots",
    "unity_migration",
    "Godot_v4.6.2-stable_mono_win64",
    "SDK",
    "RTC_Token",
    "akskdemo",
    "动作",
    "干净模型",
    "星穹铁道—银狼LV.999",
    "模型2",
    "语音包",
}

EXTRA_TEXT_FILES = (
    ".editorconfig",
    ".gitattributes",
    ".gitignore",
)

MOJIBAKE_LITERALS = (
    "\u951f\u65a4\u62f7",
    "\ufffd",
    "\u00c3",
    "\u00c2",
    "\u00e4\u00bd",
    "\u00e4\u00b8",
    "\u00e5\u00a5",
    "\u00e5\u00bf",
    "\u00e6\u02dc",
    "\u00e6\u0153",
    "\u00e6\u00b2",
    "\u6d93",
    "\u7ecb",
    "\u7f02",
    "\u7487",
    "\u93c4",
    "\u9359",
    "\u9428",
    "\u59af",
    "\u6fc2",
    "\u7ed4",
    "\u95ab",
    "\u9422",
    "\u6434",
    "\u9225",
    "\u00e2\u20ac",
    "\u00e2\u20ac\u2122",
    "\u00e2\u20ac\u0153",
    "\u00e2\u20ac\u009d",
    "\u7ec1",
    "\u943e",
    "\u6d7c",
    "\u9365",
    "\u9353",
    "\u93ac",
    "\u95c4",
    "\u5a23",
    "\u704f",
)

MOJIBAKE_REGEXES = (
    re.compile(r"\?{4,}"),
    re.compile(r"(?:[\u00c3\u00c2][\x80-\xffA-Za-z]{1,4}){2,}"),
    re.compile(
        r"(?:\u6d93|\u7ecb|\u7f02|\u7487|\u93c4|\u9359|\u9428|\u59af|\u6fc2|\u7ed4|\u95ab|\u9422|\u6434).{0,12}"
        r"(?:\u6d93|\u7ecb|\u7f02|\u7487|\u93c4|\u9359|\u9428|\u59af|\u6fc2|\u7ed4|\u95ab|\u9422|\u6434)"
    ),
)


@dataclass(frozen=True)
class Finding:
    path: Path
    line: int
    code: str
    snippet: str


def main() -> int:
    parser = argparse.ArgumentParser(description="Scan project code/config files for mojibake.")
    parser.add_argument("--root", default=str(ROOT))
    parser.add_argument(
        "--include-docs",
        action="store_true",
        help="Also scan docs, root markdown files, and root metadata files.",
    )
    args = parser.parse_args()

    root = Path(args.root).resolve()
    findings = scan(root, include_docs=args.include_docs)
    if findings:
        for finding in findings:
            rel = finding.path.relative_to(root)
            print(f"{rel}:{finding.line}: {finding.code}: {finding.snippet}")
        print(f"\nCode mojibake check failed: {len(findings)} finding(s).")
        return 1
    print("Code mojibake check passed.")
    return 0


def scan(root: Path, *, include_docs: bool = False) -> list[Finding]:
    findings: list[Finding] = []
    for path in iter_files(root, include_docs=include_docs):
        try:
            text = path.read_text(encoding="utf-8-sig")
        except UnicodeDecodeError as exc:
            findings.append(Finding(path, 1, "not_utf8", str(exc)))
            continue
        for line_no, line in enumerate(text.splitlines(), start=1):
            findings.extend(check_line(path, line_no, line))
    return findings


def iter_files(root: Path, *, include_docs: bool) -> list[Path]:
    roots = list(DEFAULT_ROOTS)
    if include_docs:
        roots.append("docs")
        roots.extend(["README.md", "PROJECT_PLAN.md", "TEST_CHECKLIST.md", "TROUBLESHOOTING.md"])
        roots.extend(EXTRA_TEXT_FILES)
    files: list[Path] = []
    for item in roots:
        path = root / item
        if path.is_file():
            if is_text_candidate(path) and not should_skip(path, root):
                files.append(path)
            continue
        if not path.exists():
            continue
        for child in path.rglob("*"):
            if not child.is_file():
                continue
            if not is_text_candidate(child):
                continue
            if should_skip(child, root):
                continue
            files.append(child)
    return sorted(set(files))


def is_text_candidate(path: Path) -> bool:
    return path.suffix.lower() in TEXT_EXTENSIONS or path.name in EXTRA_TEXT_FILES


def should_skip(path: Path, root: Path) -> bool:
    try:
        rel_parts = path.relative_to(root).parts
    except ValueError:
        return True
    if path.name.startswith("._"):
        return True
    if any(part in SKIP_DIRS for part in rel_parts):
        return True
    rel = path.relative_to(root).as_posix()
    if rel.startswith("assets/action_import/") or rel.startswith("assets/pose_import/"):
        return True
    return False


def check_line(path: Path, line_no: int, line: str) -> list[Finding]:
    stripped = line.strip()
    if not stripped:
        return []
    findings: list[Finding] = []
    for literal in MOJIBAKE_LITERALS:
        if literal in stripped:
            findings.append(Finding(path, line_no, "mojibake_literal", summarize(stripped)))
            return findings
    for regex in MOJIBAKE_REGEXES:
        if regex.search(stripped):
            if is_known_false_positive(stripped):
                continue
            findings.append(Finding(path, line_no, "mojibake_pattern", summarize(stripped)))
            return findings
    return findings


def is_known_false_positive(line: str) -> bool:
    # Question marks in English comments and SQL-style placeholders are not
    # encoding loss by themselves. Repeated question marks are suspicious only
    # when they appear as text content, not as casual punctuation.
    if line.endswith("???") and re.match(r"^#|^//|^/\*|^\*", line):
        return True
    return False


def summarize(line: str, max_len: int = 140) -> str:
    line = " ".join(line.split())
    if len(line) <= max_len:
        return line
    return line[: max_len - 3] + "..."


if __name__ == "__main__":
    raise SystemExit(main())
