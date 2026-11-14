# SlopTank modification notice: added or changed by SlopTank on 2026-11-14.
"""Validate passed native GUI observations against the same current signed app.

This module is pure: it never follows paths from a historical guest receipt.
Current signature and runtime checks remain the installed-smoke driver's job.
"""
from __future__ import annotations

import copy
import re
from typing import Any

class SmokeReuseError(RuntimeError):
    """Prior GUI observations cannot safely be attributed to this app."""


RUNTIMES = {'osx-arm64': 'arm64', 'osx-x64': 'x86_64'}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SmokeReuseError('Smoke receipt reuse refused: ' + message)


def cdhash(distribution: dict[str, Any]) -> str:
    """Read the active architecture CodeDirectory hash from codesign output."""
    signature = distribution.get('app_signature', '')
    matches = re.findall(r'^CDHash=([0-9a-f]{40,64})$', signature, re.MULTILINE)
    require(len(matches) == 1, 'signature must contain exactly one CDHash')
    require('Authority=Developer ID Application:' in signature and 'TeamIdentifier=6227ZC5AC2' in signature
            and '(runtime)' in signature and 'Timestamp=' in signature,
            'signature lacks current Developer ID, hardened runtime or timestamp')
    return matches[0]


def version_key(version: dict[str, str]) -> tuple[tuple[int, ...], ...]:
    require(all(isinstance(version.get(k), str) and version[k] for k in ('bundle_id','version','build')),
            'bundle version fields are incomplete')
    require(all(all(part.isdecimal() for part in version[k].split('.')) for k in ('version','build')),
            'bundle version fields are not numeric')
    return tuple(tuple(map(int, version[k].split('.'))) for k in ('version','build'))


def validate_browser(row: dict[str, Any], current: dict[str, str], baseline: dict[str, Any] | None,
                     *, upgrade: bool) -> None:
    browser = row.get('browser', {})
    playback = browser.get('playback', {})
    item = row.get('library_item_id')
    require(bool(item) and browser.get('schemaVersion') == 2 and browser.get('status') == 'passed'
            and browser.get('pageErrors') == [] and browser.get('itemId') == item,
            'passed browser must identify the same item and have no page errors')
    require(playback.get('advanced') is True and playback.get('error', 'missing') is None
            and playback.get('paused') is False and playback.get('readyState', 0) >= 2
            and playback.get('width', 0) > 0 and playback.get('height', 0) > 0
            and playback.get('laterTime', 0) > playback.get('currentTime', 0),
            'passed browser must contain actual decoded, advancing playback')
    require(browser.get('adminUserId') == row.get('admin_user_id')
            and browser.get('serverPlaybackReport', {}).get('nowPlayingItemId') == item
            and row.get('server_playback', {}).get('play_count', 0) >= 1,
            'browser playback must match server-side user/item observations')
    retention = row.get('playback_retention', {})
    before = retention.get('before', {})
    require(retention.get('status') == 'passed' and retention.get('item_id') == item
            and retention.get('original_permalink_resolved') is True and bool(retention.get('permalink_id'))
            and all(k in before for k in ('play_count','last_played_date','position_ticks'))
            and before == retention.get('after'), 'passed playback retention is incomplete')
    transition = row.get('version_transition' if upgrade else 'same_version_replacement', {})
    require(transition.get('to') == current, 'replacement target bundle version changed')
    old = transition.get('from', {})
    require(old.get('bundle_id') == current['bundle_id'], 'replacement bundle identity changed')
    if upgrade:
        require(isinstance(baseline, dict) and old == baseline.get('from')
                and baseline.get('to') == current and version_key(old) < version_key(current),
                'upgrade does not bind the same genuine older baseline and target')
    else:
        require(old == current, 'same-version replacement has different source/target versions')
    require(all(transition.get(key) == row.get(key) and bool(row.get(key))
                for key in ('profile_id','server_id','admin_user_id'))
            and transition.get('selected_root') == row.get('profile')
            and transition.get('library_item_retained') is True and transition.get('media_retained') is True
            and transition.get('welcome_reappeared') is False and row.get('user_defaults_bookmark_present') is False,
            'replacement did not retain the observed profile, user, media and welcome state')


def validate_alternate(row: dict[str, Any]) -> None:
    relaunch = row.get('relaunch', {})
    require(row.get('user_defaults_bookmark_present') is True and bool(row.get('preferences_capture'))
            and all(row.get(key) and relaunch.get(key) == row[key]
                    for key in ('selected_root','profile_id','server_id'))
            and relaunch.get('welcome_reappeared') is False and relaunch.get('wizard_remains_pending') is True,
            'alternate-root bookmark and relaunch observations are incomplete')


def reusable_journeys(prior: dict[str, Any], *, distribution: dict[str, Any], runtime_id: str,
                      bundle_version: dict[str, str], baseline: dict[str, Any] | None,
                      dmg_sha256: str) -> dict[str, Any]:
    """Return only independently passed GUI rows for the same app/runtime.

    A partially failed receipt is allowed; its failed/running rows are not
    reused. A row claiming to pass with invalid observations is rejected.
    """
    require(prior.get('schemaVersion') == 1 and prior.get('type') == 'sloptank-installed-app-smoke',
            'unexpected smoke receipt schema/type')
    require(prior.get('cleanup_errors') == [], 'original cleanup was not successful')
    require(runtime_id in RUNTIMES and prior.get('machine') == RUNTIMES[runtime_id], 'runtime machine changed')
    gui = prior.get('gui_acceptance', {})
    require(gui.get('scope') == 'GitHub macOS guest only' and gui.get('runtime_id') == runtime_id,
            'GUI runtime/scope changed')
    require(gui.get('bundle_id') == bundle_version.get('bundle_id'), 'bundle identity changed')
    version_key(bundle_version)
    require(cdhash(prior.get('distribution', {})) == cdhash(distribution), 'current app CDHash differs')
    require(not prior.get('dmg_sha256') or prior['dmg_sha256'] == dmg_sha256, 'DMG hash changed')
    result = {}
    for name in ('default_root','upgrade','alternate_root'):
        row = gui.get(name, {})
        if row.get('status') != 'passed':
            continue
        if name == 'alternate_root':
            validate_alternate(row)
        else:
            if name == 'upgrade':
                old_baseline = prior.get('upgrade_baseline', {})
                require(isinstance(baseline, dict)
                        and cdhash(old_baseline.get('signature', {})) == cdhash(baseline.get('signature', {})),
                        'upgrade baseline CDHash differs')
            validate_browser(row, bundle_version, baseline, upgrade=name == 'upgrade')
        result[name] = copy.deepcopy(row)
    require(bool(result), 'receipt has no reusable passed GUI journeys')
    return result
