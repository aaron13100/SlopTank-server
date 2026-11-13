#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
"""Move verified preparation source archives into a separate release asset."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import tarfile
import tempfile
from typing import Any
from urllib.parse import urlparse


def digest(path: Path) -> str:
    """Return the SHA-256 of a file without loading it into memory."""
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def validate(materials: Path, output: Path, version: str) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    """Validate source records, exact directory population and destination."""
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+', version):
        raise ValueError(f'Invalid release version: {version}')
    if output.name != f'SlopTank-{version}-preparation-corresponding-source.tar.gz':
        raise ValueError(f'Unexpected source asset name: {output}')
    resolved_output = output.resolve()
    if os.path.lexists(output) or resolved_output.is_relative_to(materials.resolve()) or any(p.suffix == '.app' for p in resolved_output.parents):
        raise ValueError(f'Source asset must be new and outside the app and materials: {output}')
    source_dir = materials / 'preparation-sources'
    if source_dir.is_symlink() or not source_dir.is_dir():
        raise ValueError(f'Invalid preparation source directory: {source_dir}')
    manifest = materials / 'sources.json'
    if manifest.is_symlink() or not manifest.is_file():
        raise ValueError(f'Invalid source manifest: {manifest}')
    document = json.loads(manifest.read_text(encoding='utf-8'))
    if 'preparationCorrespondingSourceKit' in document:
        raise ValueError(f'Preparation source kit already recorded: {manifest}')
    records = document['preparationSources']
    if not isinstance(records, list) or not records:
        raise ValueError('Preparation source records must be a nonempty list')
    names = set()
    for record in records:
        if not isinstance(record, dict):
            raise ValueError('Invalid preparation source record')
        name, package_name, package_version, url, sha = (record.get(k) for k in ('file', 'name', 'version', 'url', 'sha256'))
        if (not all(isinstance(v, str) and v for v in (name, package_name, package_version, url, sha))
                or name in ('.', '..') or '/' in name or '\\' in name or name in names
                or urlparse(url).scheme != 'https' or not urlparse(url).netloc
                or not re.fullmatch(r'[0-9a-f]{64}', sha)):
            raise ValueError(f'Invalid preparation source record: {record}')
        names.add(name)
        path = source_dir / name
        if not stat.S_ISREG(path.lstat().st_mode) or digest(path) != sha:
            raise ValueError(f'Preparation source is not a matching regular file: {path}')
    if {p.name for p in source_dir.iterdir()} != names:
        raise ValueError(f'Unexpected preparation source directory contents: {source_dir}')
    if (materials / 'build-recipes').is_symlink():
        raise ValueError(f'Recipe directory is a symlink: {materials / "build-recipes"}')
    for path in (materials / 'PREPARATION-SOURCE-ACCESS.txt', materials / 'build-recipes/source_assets.py'):
        if os.path.lexists(path):
            raise ValueError(f'Output already exists: {path}')
    return document, records


def create_kit(source_dir: Path, records: list[dict[str, Any]], output: Path) -> None:
    """Create, verify and atomically publish the unchanged source archives."""
    fd, temporary = tempfile.mkstemp(prefix='.preparation-source-', suffix='.tar.gz', dir=output.parent)
    temp = Path(temporary)
    os.close(fd)
    try:
        with tarfile.open(temp, 'w:gz') as archive:
            for record in records:
                archive.add(source_dir / record['file'], arcname='preparation-sources/' + record['file'], recursive=False)
        with tarfile.open(temp, 'r:gz') as archive:
            members = archive.getmembers()
            expected = {'preparation-sources/' + record['file']: record['sha256'] for record in records}
            if len(members) != len(expected) or {member.name for member in members} != set(expected):
                raise ValueError(f'Source asset member mismatch: {temp}')
            for member in members:
                if not member.isfile():
                    raise ValueError(f'Invalid source asset member: {member.name}')
                with archive.extractfile(member) as stream:
                    if hashlib.file_digest(stream, 'sha256').hexdigest() != expected[member.name]:
                        raise ValueError(f'Source asset hash mismatch: {member.name}')
        os.link(temp, output)
    finally:
        temp.unlink(missing_ok=True)


def package(materials: Path, output: Path, version: str) -> None:
    """Publish a verified source kit and replace embedded archives with an offer."""
    document, records = validate(materials, output, version)
    source_dir = materials / 'preparation-sources'
    create_kit(source_dir, records, output)
    url = f'https://github.com/aaron13100/SlopTank-server/releases/download/v{version}/{output.name}'
    recipe = materials / 'build-recipes/source_assets.py'
    recipe.parent.mkdir(exist_ok=True)
    shutil.copyfile(Path(__file__), recipe)
    kit = {'schema': 'org.sloptank.preparation-corresponding-source/v1', 'version': 1,
           'file': output.name, 'url': url, 'sha256': digest(output),
           'entries': [{'file': 'preparation-sources/' + record['file'], 'sha256': record['sha256']} for record in records],
           'created_at': datetime.now(timezone.utc).isoformat(),
           'recipe': {'file': 'build-recipes/source_assets.py', 'sha256': digest(recipe)}}
    document['preparationCorrespondingSourceKit'] = kit
    (materials / 'PREPARATION-SOURCE-ACCESS.txt').write_text(
        f'Preparation corresponding source: {url}\nSHA-256: {kit["sha256"]}\n'
        'The archive contains unchanged upstream files under preparation-sources/. '
        'Their original URLs and hashes remain in sources.json.\n'
        'To reproduce: retain build-recipes/source_assets.py, then build from the '
        'serverAndInstaller commit in sources.json; before signing, run python3 '
        'PATH_TO_RETAINED_RECIPE --materials-dir PATH_TO_FRESH_RELEASE_MATERIALS '
        '--output PATH_OUTSIDE_APP/' + output.name +
        ' --version ' + version + '. The recipe and its SHA-256 are recorded in sources.json.\n', encoding='utf-8')
    (materials / 'sources.json').write_text(json.dumps(document, indent=2) + '\n', encoding='utf-8')
    for record in records:
        (source_dir / record['file']).unlink()
    source_dir.rmdir()


def main() -> None:
    """Package a release's preparation sources outside the app."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--materials-dir', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--version', required=True)
    args = parser.parse_args()
    package(args.materials_dir, args.output, args.version)


if __name__ == '__main__':
    main()
