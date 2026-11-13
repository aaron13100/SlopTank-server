#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
"""Validate retained macOS build inputs against GitHub and Git history."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
from typing import Any

WORKFLOW = '.github/workflows/macos-universal.yml'
SCHEMA = 'org.sloptank.macos-candidate-inputs/v1'
PINS = ('WEB_REF', 'RELEASE_VERSION', 'FFMPEG_X64_BUILD_RUN_ID', 'FFMPEG_ARM64_BUILD_RUN_ID')
JOBS = ('web', 'native (macos-15, osx-arm64, arm64)', 'native (macos-15-intel, osx-x64, x86_64)')
ARTIFACTS = ('web', 'native-osx-arm64', 'native-osx-x64')
ALLOWED = {WORKFLOW, 'apps/macos/candidate_inputs.py', 'apps/macos/source_assets.py', 'apps/macos/smoke-installed-app.py',
           'apps/macos/smoke-browser-ui.cjs', 'apps/macos/welcome-ui.js',
           'apps/macos/owned_app_processes.py', 'apps/macos/smoke_deadline.py',
           'apps/macos/smoke_server_api.py', 'apps/macos/fetch-release-asset.py',
           'apps/macos/playback_retention.py', 'apps/macos/macos_distribution.py',
           'apps/macos/SLOPTANK-MODIFICATION-NOTICES.txt'}


def require(condition: bool, message: str) -> None:
    """Raise a diagnostic when a provenance requirement fails."""
    if not condition:
        raise ValueError(message)


def git(*args: str) -> str:
    """Read a Git result from the checked-out workflow history."""
    return subprocess.check_output(['git', *args], text=True, timeout=30).strip()


def api(path: str) -> dict[str, Any]:
    """Read a GitHub API response with the configured token."""
    return json.loads(subprocess.check_output(['gh', 'api', path], text=True, timeout=30))


def run_data(run_id: str) -> tuple[str, dict[str, Any]]:
    """Check the run identity and return its API endpoint and metadata."""
    require(bool(re.fullmatch(r'[1-9][0-9]*', str(run_id))), 'Run ID must be a positive integer')
    endpoint = f"repos/{os.environ['GH_REPO']}/actions/runs/{run_id}"
    run = api(endpoint)
    require(str(run['id']) == str(run_id) and run['path'] == WORKFLOW, 'Wrong workflow or run')
    return endpoint, run


def pages(endpoint: str, resource: str, extra: str = '') -> list[dict[str, Any]]:
    """Read every API page for a run resource."""
    result = []
    page = 1
    while True:
        items = api(f'{endpoint}/{resource}?per_page=100&page={page}{extra}')[resource]
        result.extend(items)
        if len(items) < 100:
            return result
        page += 1


def require_jobs(endpoint: str, names: tuple[str, ...]) -> None:
    """Require the latest attempt of each named job to have succeeded."""
    latest = {}
    for job in pages(endpoint, 'jobs', '&filter=all'):
        key = (job.get('run_attempt', 1), job['id'])
        if job['name'] not in latest or key > latest[job['name']][0]:
            latest[job['name']] = (key, job)
    for name in names:
        require(name in latest and latest[name][1]['status'] == 'completed'
                and latest[name][1]['conclusion'] == 'success', f'Required job did not pass: {name}')


def check_source(source: str, target: str) -> dict[str, str]:
    """Check ancestry, harness-only differences, and the four pinned inputs."""
    require(bool(re.fullmatch(r'[0-9a-f]{40}', source)) and bool(re.fullmatch(r'[0-9a-f]{40}', target)), 'Invalid source commit')
    subprocess.run(['git', 'merge-base', '--is-ancestor', source, target], check=True, timeout=30)
    changed = set(git('diff', '--no-renames', '--name-only', source, target).splitlines())
    require(not changed - ALLOWED, f'Product files changed: {sorted(changed - ALLOWED)}')
    workflow = git('show', f'{source}:{WORKFLOW}')
    pins = {}
    for name in PINS:
        match = re.search(r'^  ' + name + r':\s*(.+)$', workflow, re.MULTILINE)
        require(bool(match), f'Missing pin: {name}')
        pins[name] = match.group(1).strip().strip('"\'')
        require(pins[name] == os.environ[name], f'Build input differs: {name}')
    return pins


def component_selection(run_id: str, workflow_commit: str, current: bool = False) -> dict[str, Any]:
    """Validate component jobs and artifacts and return their provenance."""
    endpoint, run = run_data(run_id)
    require(run['head_branch'] == 'release-universal', 'Components must come from release-universal')
    require(run['status'] == 'completed' or (current and run['status'] == 'in_progress'), 'Component run is not completed')
    source = run['head_sha']
    pins = check_source(source, workflow_commit)
    require_jobs(endpoint, JOBS)
    components = {}
    artifacts = pages(endpoint, 'artifacts')
    for name in ARTIFACTS:
        matches = [item for item in artifacts if item['name'] == name and not item['expired']]
        require(len(matches) == 1, f'Expected one unexpired artifact: {name}')
        item = matches[0]
        binding = item['workflow_run']
        require(str(binding['id']) == str(run_id) and binding['head_sha'] == source, f'Wrong artifact binding: {name}')
        require(isinstance(item['id'], int) and item['id'] > 0 and bool(re.fullmatch(r'sha256:[0-9a-f]{64}', item.get('digest') or '')), f'Invalid artifact identity: {name}')
        components[name] = {'id': item['id'], 'digest': item['digest']}
    return {'schema': SCHEMA, 'product_source_commit': source, 'workflow_commit': workflow_commit,
            'component_run_id': str(run_id), 'pins': pins, 'components': components}


def candidate_reuse(run_id: str, manifest_path: Path, head: str) -> None:
    """Validate a candidate and its original component provenance."""
    endpoint, run = run_data(run_id)
    require(run['status'] == 'completed', 'Candidate run must be completed')
    require_jobs(endpoint, ('assemble',))
    if manifest_path.exists():
        manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
        require(manifest.get('schema') == SCHEMA and manifest.get('workflow_commit') == run['head_sha'], 'Invalid candidate manifest or workflow commit')
        expected = component_selection(manifest['component_run_id'], run['head_sha'])
        require(all(manifest.get(key) == value for key, value in expected.items()),
                'Candidate manifest does not match component provenance')
        check_source(manifest['product_source_commit'], head)
    else:
        require(run['head_branch'] == 'release-universal', 'Legacy candidate must be from release-universal')
        require_jobs(endpoint, JOBS)
        check_source(run['head_sha'], head)
    print(f'Validated candidate run {run_id}')


def main() -> None:
    """Run the requested component-selection or candidate-reuse operation."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('component-selection', 'candidate-reuse'))
    parser.add_argument('--run-id', required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    args = parser.parse_args()
    head = git('rev-parse', 'HEAD')
    if args.operation == 'component-selection':
        result = component_selection(args.run_id, head, args.run_id == os.environ['GITHUB_RUN_ID'])
        args.manifest.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
        print(result['product_source_commit'])
    else:
        candidate_reuse(args.run_id, args.manifest, head)


if __name__ == '__main__':
    main()
