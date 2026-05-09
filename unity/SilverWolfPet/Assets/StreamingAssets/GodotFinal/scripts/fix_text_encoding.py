from __future__ import annotations

import argparse
import shutil
from dataclasses import dataclass
from pathlib import Path

from check_text_encoding import ROOT, TEXT_EXTENSIONS, UTF16BE_BOM, UTF16LE_BOM, UTF8_BOM, iter_text_files


@dataclass(frozen=True)
class ConvertResult:
    path: Path
    source_encoding: str
    target_encoding: str
    backup_path: Path


def main() -> int:
    parser = argparse.ArgumentParser(description="Convert repository text files to the project encoding policy.")
    parser.add_argument("--root", default=str(ROOT), help="Repository root to scan.")
    parser.add_argument("--dry-run", action="store_true", help="Print planned conversions without writing files.")
    args = parser.parse_args()

    root = Path(args.root).resolve()
    changed: list[ConvertResult] = []
    for path in iter_text_files(root):
        result = convert_file(path, dry_run=args.dry_run)
        if result is not None:
            changed.append(result)

    for item in changed:
        rel = item.path.relative_to(root)
        backup = item.backup_path.relative_to(root)
        print(f"{rel}: {item.source_encoding} -> {item.target_encoding} backup={backup}")

    action = "would convert" if args.dry_run else "converted"
    print(f"Encoding fixer {action} {len(changed)} file(s).")
    return 0


def convert_file(path: Path, *, dry_run: bool = False) -> ConvertResult | None:
    if path.suffix.lower() not in TEXT_EXTENSIONS:
        return None

    data = path.read_bytes()
    text, source_encoding = decode_text(data)
    text = normalize_newlines(text)

    target_encoding = "utf-8-sig" if path.suffix.lower() == ".ps1" else "utf-8"
    target_data = text.encode(target_encoding)
    if data == target_data:
        return None

    backup_path = next_backup_path(path)
    if not dry_run:
        shutil.copy2(path, backup_path)
        path.write_bytes(target_data)

    return ConvertResult(path, source_encoding, target_encoding, backup_path)


def decode_text(data: bytes) -> tuple[str, str]:
    if data.startswith(UTF16LE_BOM) or data.startswith(UTF16BE_BOM):
        return data.decode("utf-16"), "utf-16"
    if data.startswith(UTF8_BOM):
        return data.decode("utf-8-sig"), "utf-8-sig"
    try:
        return data.decode("utf-8"), "utf-8"
    except UnicodeDecodeError:
        return data.decode("gb18030"), "gb18030"


def normalize_newlines(text: str) -> str:
    return text.replace("\r\n", "\n").replace("\r", "\n")


def next_backup_path(path: Path) -> Path:
    candidate = path.with_name(path.name + ".bak")
    if not candidate.exists():
        return candidate
    index = 1
    while True:
        numbered = path.with_name(f"{path.name}.bak.{index}")
        if not numbered.exists():
            return numbered
        index += 1


if __name__ == "__main__":
    raise SystemExit(main())
