from __future__ import annotations

import argparse
import ast
import re
import sys
from dataclasses import dataclass
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
TEXT_EXTENSIONS = {
    ".py",
    ".gd",
    ".cs",
    ".ts",
    ".tsx",
    ".js",
    ".json",
    ".yaml",
    ".yml",
    ".md",
    ".txt",
    ".ps1",
}
SKIP_DIRS = {
    ".git",
    ".godot",
    ".tmp",
    ".venv",
    ".vs",
    "backups",
    "node_modules",
    "logs",
    "__MACOSX",
    "bin",
    "obj",
    "Library",
    "site-packages",
    "head_tracker",
    "unity_migration",
    "干净模型",
    "模型2",
    "Godot_v4.6.2-stable_mono_win64",
    "SDK",
}
UTF8_BOM = b"\xef\xbb\xbf"
UTF16LE_BOM = b"\xff\xfe"
UTF16BE_BOM = b"\xfe\xff"

MOJIBAKE_PATTERNS = [
    "\u951f\u65a4\u62f7",
    "\ufffd",
    "\u00e4\u00bd",
    "\u00e6",
    # Common UTF-8 Chinese text decoded as GBK/ANSI, seen in Windows docs/paths.
    "\u93c4",
    "\u95be",
    "\u9225",
    "\u9419",
    "\u74ba",
    "\u7eef",
    "\u93cc",
    "\u934b",
    "\u935b",
    "\u9356",
    "\u6d93",
    "\u93b4",
    "\u6d63",
    "\u59ab",
    "\u5bee",
    "\u9354",
    "\u7487",
    "\u95bf",
    "\ue5cd",
    "\ue5bd",
]


@dataclass(frozen=True)
class Issue:
    path: Path
    code: str
    detail: str


def main() -> int:
    parser = argparse.ArgumentParser(description="Check repository text encodings.")
    parser.add_argument("--root", default=str(ROOT), help="Repository root to scan.")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    issues = scan(root)
    if issues:
        for issue in issues:
            rel = issue.path.relative_to(root)
            print(f"{rel}: {issue.code}: {issue.detail}")
        print(f"\nEncoding check failed: {len(issues)} issue(s).")
        return 1

    print("Encoding check passed.")
    return 0


def scan(root: Path) -> list[Issue]:
    issues: list[Issue] = []
    for path in iter_text_files(root):
        try:
            data = path.read_bytes()
        except FileNotFoundError:
            continue
        issues.extend(check_file(path, data))
    return issues


def iter_text_files(root: Path) -> list[Path]:
    files: list[Path] = []
    for path in root.rglob("*"):
        if not path.is_file():
            continue
        if path.suffix.lower() not in TEXT_EXTENSIONS:
            continue
        if should_skip(path, root):
            continue
        files.append(path)
    return sorted(files)


def should_skip(path: Path, root: Path) -> bool:
    try:
        rel_parts = path.relative_to(root).parts
    except ValueError:
        return True
    return path.name.startswith("._") or any(part in SKIP_DIRS for part in rel_parts)


def check_file(path: Path, data: bytes) -> list[Issue]:
    issues: list[Issue] = []
    suffix = path.suffix.lower()

    if data.startswith(UTF16LE_BOM):
        issues.append(Issue(path, "utf16le_bom", "UTF-16LE BOM is not allowed; convert to UTF-8."))
        return issues
    if data.startswith(UTF16BE_BOM):
        issues.append(Issue(path, "utf16be_bom", "UTF-16BE BOM is not allowed; convert to UTF-8."))
        return issues

    has_utf8_bom = data.startswith(UTF8_BOM)
    try:
        text = data.decode("utf-8-sig" if has_utf8_bom else "utf-8")
    except UnicodeDecodeError as exc:
        issues.append(Issue(path, "not_utf8", str(exc)))
        return issues

    if suffix == ".ps1" and not has_utf8_bom:
        issues.append(Issue(path, "ps1_missing_utf8_bom", "PowerShell scripts must be UTF-8 with BOM."))
    elif suffix != ".ps1" and has_utf8_bom:
        issues.append(Issue(path, "unexpected_utf8_bom", "Only .ps1 files should keep a UTF-8 BOM."))

    if "\r\n" in text or "\r" in text.replace("\r\n", ""):
        issues.append(Issue(path, "non_lf_newline", "Use LF line endings."))

    for pattern in MOJIBAKE_PATTERNS:
        if pattern in text:
            issues.append(Issue(path, "mojibake", f"Suspicious text fragment: {pattern!r}"))
            break
    if re.search(r"\?{4,}", text):
        issues.append(Issue(path, "mojibake_question_marks", "Suspicious repeated question marks; possible lossy ANSI/GBK conversion."))

    if suffix == ".py":
        issues.extend(check_python_text_io(path, text))
    elif suffix in {".js", ".ts", ".tsx"}:
        issues.extend(check_node_text_io(path, text))
    elif suffix == ".ps1":
        issues.extend(check_powershell_text_io(path, text))

    return issues


def check_python_text_io(path: Path, text: str) -> list[Issue]:
    issues: list[Issue] = []
    try:
        tree = ast.parse(text, filename=str(path))
    except SyntaxError as exc:
        return [Issue(path, "python_parse_error", str(exc))]

    for node in ast.walk(tree):
        if not isinstance(node, ast.Call):
            continue
        if is_builtin_open_call(node) or is_path_open_call(node):
            if call_uses_binary_mode(node):
                continue
            if not has_keyword(node, "encoding"):
                issues.append(Issue(path, "python_missing_encoding", f"line {node.lineno}: text open() needs encoding."))
        if is_read_write_text_call(node) and not has_keyword(node, "encoding"):
            issues.append(Issue(path, "python_missing_encoding", f"line {node.lineno}: read_text/write_text needs encoding."))
    return issues


def is_builtin_open_call(node: ast.Call) -> bool:
    return isinstance(node.func, ast.Name) and node.func.id == "open"


def is_path_open_call(node: ast.Call) -> bool:
    if not isinstance(node.func, ast.Attribute) or node.func.attr != "open":
        return False
    if isinstance(node.func.value, ast.Name) and node.func.value.id in {"webbrowser"}:
        return False
    return True


def is_read_write_text_call(node: ast.Call) -> bool:
    return isinstance(node.func, ast.Attribute) and node.func.attr in {"read_text", "write_text"}


def call_uses_binary_mode(node: ast.Call) -> bool:
    mode_node: ast.AST | None = None
    if len(node.args) >= 2:
        mode_node = node.args[1]
    for keyword in node.keywords:
        if keyword.arg == "mode":
            mode_node = keyword.value
            break
    return isinstance(mode_node, ast.Constant) and isinstance(mode_node.value, str) and "b" in mode_node.value


def has_keyword(node: ast.Call, keyword_name: str) -> bool:
    return any(keyword.arg == keyword_name for keyword in node.keywords)


def check_node_text_io(path: Path, text: str) -> list[Issue]:
    issues: list[Issue] = []
    for line_number, line in enumerate(text.splitlines(), start=1):
        if "readFileSync" in line and not re.search(r"['\"]utf8['\"]|encoding\s*:\s*['\"]utf8['\"]", line):
            issues.append(Issue(path, "node_missing_utf8", f"line {line_number}: readFileSync needs utf8."))
        if "writeFileSync" in line and not re.search(r"['\"]utf8['\"]|encoding\s*:\s*['\"]utf8['\"]", line):
            issues.append(Issue(path, "node_missing_utf8", f"line {line_number}: writeFileSync needs utf8."))
    return issues


def check_powershell_text_io(path: Path, text: str) -> list[Issue]:
    issues: list[Issue] = []
    for line_number, line in enumerate(text.splitlines(), start=1):
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if re.search(r"\b(Get-Content|Set-Content|Add-Content|Out-File)\b", stripped, re.IGNORECASE) and not re.search(
            r"(^|\s)-Encoding(\s|$)", stripped, re.IGNORECASE
        ):
            issues.append(Issue(path, "powershell_missing_encoding", f"line {line_number}: text reads/writes need -Encoding."))
        redirection_check = re.sub(r"\d\s*>\s*&\s*1|\d\s*>\s*\$null", "", stripped)
        if ">>" in redirection_check or re.search(r"(^|[^-<>=])>([^&=]|$)", redirection_check):
            issues.append(Issue(path, "powershell_redirection_write", f"line {line_number}: avoid > or >> for text writes."))
    return issues


if __name__ == "__main__":
    raise SystemExit(main())
