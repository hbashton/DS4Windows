#!/usr/bin/env python3
"""Generate deterministic WiX file components and a hashed package manifest."""

from __future__ import annotations

import argparse
import hashlib
import json
import uuid
from pathlib import Path
from xml.sax.saxutils import escape


NAMESPACE = uuid.UUID("aa1633b0-7cbc-4592-b633-0ce627aff935")


def wix_id(prefix: str, value: str) -> str:
    digest = hashlib.sha256(value.encode("utf-8")).hexdigest()[:24]
    return f"{prefix}_{digest}"


def xml(value: str) -> str:
    return escape(value, {'"': '&quot;'})


def digest(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(block)
    return hasher.hexdigest().upper()


def emit_directory(lines: list[str], root: Path, directory: Path, files: list[Path], indent: str) -> list[str]:
    refs: list[str] = []
    rel_dir = directory.relative_to(root).as_posix()
    for file_path in sorted((p for p in files if p.parent == directory), key=lambda p: p.name.lower()):
        rel = file_path.relative_to(root).as_posix()
        component_id = wix_id("cmp", rel)
        file_id = "fil_DS4Windows_exe" if rel.lower() == "ds4windows.exe" else wix_id("fil", rel)
        guid = str(uuid.uuid5(NAMESPACE, rel.lower())).upper()
        lines.append(f'{indent}<Component Id="{component_id}" Guid="{guid}">')
        lines.append(f'{indent}  <File Id="{file_id}" Source="$(var.PublishRoot)\\{xml(rel.replace("/", chr(92)))}" KeyPath="yes" />')
        lines.append(f'{indent}</Component>')
        refs.append(component_id)

    children = sorted({p.parent for p in files if p.parent.parent == directory}, key=lambda p: p.name.lower())
    for child in children:
        child_rel = child.relative_to(root).as_posix()
        lines.append(f'{indent}<Directory Id="{wix_id("dir", child_rel)}" Name="{xml(child.name)}">')
        refs.extend(emit_directory(lines, root, child, files, indent + "  "))
        lines.append(f'{indent}</Directory>')
    return refs


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("publish_root", type=Path)
    parser.add_argument("wix_output", type=Path)
    parser.add_argument("manifest_output", type=Path)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()

    root = args.publish_root.resolve()
    if not (root / "DS4Windows.exe").is_file():
        raise SystemExit(f"Publish root is incomplete: {root}")

    manifest_path = args.manifest_output.resolve()
    files = sorted(
        (path for path in root.rglob("*") if path.is_file() and path.resolve() != manifest_path),
        key=lambda p: p.relative_to(root).as_posix().lower(),
    )
    manifest = {
        "schema": 1,
        "product": "DS4Windows",
        "version": args.version,
        "architecture": "x64",
        "files": [
            {
                "path": path.relative_to(root).as_posix(),
                "size": path.stat().st_size,
                "sha256": digest(path),
            }
            for path in files
        ],
    }
    args.manifest_output.parent.mkdir(parents=True, exist_ok=True)
    args.manifest_output.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    if manifest_path.is_relative_to(root):
        files.append(manifest_path)
        files.sort(key=lambda p: p.relative_to(root).as_posix().lower())

    lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">',
        '  <Fragment>',
        '    <DirectoryRef Id="INSTALLFOLDER">',
    ]
    component_refs = emit_directory(lines, root, root, files, "      ")
    lines.extend([
        '    </DirectoryRef>',
        '  </Fragment>',
        '  <Fragment>',
        '    <ComponentGroup Id="PublishedFiles">',
    ])
    lines.extend(f'      <ComponentRef Id="{component_id}" />' for component_id in component_refs)
    lines.extend([
        '    </ComponentGroup>',
        '  </Fragment>',
        '</Wix>',
        '',
    ])
    args.wix_output.parent.mkdir(parents=True, exist_ok=True)
    args.wix_output.write_text("\n".join(lines), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
