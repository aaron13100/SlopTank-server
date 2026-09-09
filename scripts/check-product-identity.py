#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-09-08, 2026-09-09.
"""Debrand regression guard: block Jellyfin product-identity branding.

SlopTank is a fork of Jellyfin. The upstream trademark policy
(https://jellyfin.org/docs/project/branding/) requires a public fork to use its
own name and logo, while explicitly permitting descriptive references to
Jellyfin and requiring (via GPL-2.0) that upstream attribution stays intact.
The one-time rename cannot hold on its own: every upstream merge reintroduces
identity branding (README heading and logo banner, AssemblyProduct, the donate
link, the tagline, localized product strings, the fin logo assets). This guard
runs in public-tier CI and fails the build when any of those return.

Policy lives in scripts/product-identity-manifest.json next to this script:
identity regexes over tracked text-file lines, forbidden asset paths, forbidden
blob hashes, and an allowlist that keeps compatibility identifiers and GPL
attribution unflaggable. The manifest carries its own executable samples and is
self-tested before every scan, so a policy that stops doing what it claims
fails the build instead of silently passing it.

This file ships byte-identically in both public product repositories
(SlopTank and SlopTank-server); keep it free of brand literals so the policy
stays entirely in the manifest.

Usage:
  scripts/check-product-identity.py --self-test
  scripts/check-product-identity.py [--repo DIR]            # scan tracked files
  scripts/check-product-identity.py [--repo DIR] --paths P...

Exit codes: 0 clean, 1 identity violation, 2 configuration error (fails closed).
"""

import argparse
import hashlib
import json
import pathlib
import re
import subprocess
import sys
from collections import namedtuple

SUPPORTED_SCHEMA_VERSION = 1
MANIFEST_NAME = "product-identity-manifest.json"
EXIT_OK = 0
EXIT_VIOLATION = 1
EXIT_CONFIG_ERROR = 2

# A NUL byte in the leading window marks a file as binary: text rules skip it,
# blob-hash rules still see every byte.
_BINARY_SNIFF_BYTES = 8192

Violation = namedtuple("Violation", "path line_no rule_id title excerpt")
_ContentRule = namedtuple("_ContentRule", "rule_id title reason paths regex")
_PathRule = namedtuple("_PathRule", "rule_id title reason regex")
_BlobRule = namedtuple("_BlobRule", "rule_id title reason sha256")
_AllowEntry = namedtuple("_AllowEntry", "entry_id path_regex line_regex")
Policy = namedtuple("Policy", "content_rules path_rules blob_rules allowlist")


class ProductIdentityError(Exception):
    """Typed failure for every policy error path.

    Carries a stable ``code`` so callers can branch, a human ``message``, an
    actionable ``hint``, and the underlying ``cause`` when one exists.
    """

    def __init__(self, code, message, hint=None, cause=None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.hint = hint
        self.cause = cause

    def __str__(self):
        parts = ["[{}] {}".format(self.code, self.message)]
        if self.hint:
            parts.append("hint: {}".format(self.hint))
        if self.cause is not None:
            parts.append("cause: {}".format(self.cause))
        return " | ".join(parts)


def _compile(pattern, rule_id):
    """Compile one manifest pattern or surface its rule identity in the error."""
    try:
        return re.compile(pattern, re.IGNORECASE)
    except re.error as exc:
        raise ProductIdentityError(
            "manifest-bad-regex",
            "Rule {} has an invalid regex: {!r}".format(rule_id, pattern),
            hint="Fix the pattern; an uncompilable rule would leave a hole in the policy.",
            cause=exc,
        )


def load_manifest(manifest_path):
    """Read and validate the policy manifest, failing closed on any defect."""
    try:
        text = manifest_path.read_text(encoding="utf-8")
    except OSError as exc:
        raise ProductIdentityError(
            "manifest-unreadable",
            "Cannot read identity manifest at {}".format(manifest_path),
            hint="The guard fails closed. Restore the manifest or fix its permissions.",
            cause=exc,
        )
    try:
        manifest = json.loads(text)
    except ValueError as exc:
        raise ProductIdentityError(
            "manifest-invalid-json",
            "Identity manifest at {} is not valid JSON".format(manifest_path),
            hint="Fix the JSON syntax; the guard cannot evaluate an unparseable policy.",
            cause=exc,
        )
    _validate_manifest(manifest, manifest_path)
    return manifest


def _validate_manifest(manifest, manifest_path):
    """Reject documents that cannot provide a complete fail-closed policy."""
    if not isinstance(manifest, dict):
        raise ProductIdentityError(
            "manifest-not-object",
            "Identity manifest at {} must be a JSON object".format(manifest_path),
        )
    version = manifest.get("schema_version")
    if version != SUPPORTED_SCHEMA_VERSION:
        raise ProductIdentityError(
            "manifest-schema-unsupported",
            "Manifest schema_version {!r} is not supported (expected {})".format(
                version, SUPPORTED_SCHEMA_VERSION
            ),
            hint="Upgrade check-product-identity.py before using a newer manifest.",
        )
    rules = manifest.get("identity_rules")
    if not isinstance(rules, list) or not rules:
        raise ProductIdentityError(
            "manifest-no-rules",
            "Manifest declares no identity_rules",
            hint="An empty policy would silently allow everything; that is never intended.",
        )
    for rule in rules:
        for field in ("id", "title", "pattern", "reason"):
            if not rule.get(field):
                raise ProductIdentityError(
                    "manifest-rule-incomplete",
                    "Rule {} is missing required field {!r}".format(rule.get("id", "<unnamed>"), field),
                )
        if not rule.get("must_flag"):
            raise ProductIdentityError(
                "manifest-rule-untested",
                "Rule {} declares no must_flag samples".format(rule["id"]),
                hint="Every rule carries executable proof of what it blocks.",
            )
    for rule in manifest.get("forbidden_paths", []):
        for field in ("id", "title", "pattern", "reason"):
            if not rule.get(field):
                raise ProductIdentityError(
                    "manifest-rule-incomplete",
                    "Path rule {} is missing required field {!r}".format(rule.get("id", "<unnamed>"), field),
                )
    for blob in manifest.get("forbidden_blobs", []):
        digest = blob.get("sha256", "")
        if not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise ProductIdentityError(
                "manifest-bad-blob-hash",
                "Blob rule {} has an invalid sha256 {!r}".format(blob.get("id", "<unnamed>"), digest),
            )
    for entry in manifest.get("allowlist", []):
        if not entry.get("path_pattern") and not entry.get("line_pattern"):
            raise ProductIdentityError(
                "manifest-allow-entry-empty",
                "Allowlist entry {} constrains nothing".format(entry.get("id", "<unnamed>")),
                hint="Give it a path_pattern, a line_pattern, or both.",
            )


def compile_policy(manifest):
    """Compile a validated manifest into an immutable Policy matcher."""
    content_rules = [
        _ContentRule(
            rule["id"],
            rule["title"],
            rule["reason"],
            _compile(rule["paths"], rule["id"]) if rule.get("paths") else None,
            _compile(rule["pattern"], rule["id"]),
        )
        for rule in manifest["identity_rules"]
    ]
    path_rules = [
        _PathRule(rule["id"], rule["title"], rule["reason"], _compile(rule["pattern"], rule["id"]))
        for rule in manifest.get("forbidden_paths", [])
    ]
    blob_rules = [
        _BlobRule(blob["id"], blob["title"], blob["reason"], blob["sha256"])
        for blob in manifest.get("forbidden_blobs", [])
    ]
    allowlist = [
        _AllowEntry(
            entry.get("id", "PIX-?"),
            _compile(entry["path_pattern"], entry.get("id", "PIX-?")) if entry.get("path_pattern") else None,
            _compile(entry["line_pattern"], entry.get("id", "PIX-?")) if entry.get("line_pattern") else None,
        )
        for entry in manifest.get("allowlist", [])
    ]
    return Policy(content_rules, path_rules, blob_rules, allowlist)


def normalize_path(raw):
    """Return the POSIX, repo-relative form used for matching."""
    text = str(raw).replace("\\", "/").strip()
    while text.startswith("./"):
        text = text[2:]
    return text.lstrip("/")


def _allowed(policy, path, line):
    """True when an allowlist entry exempts this (path, line) pair."""
    for entry in policy.allowlist:
        if entry.path_regex is not None and not entry.path_regex.search(path):
            continue
        if entry.line_regex is not None and (line is None or not entry.line_regex.search(line)):
            continue
        return True
    return False


def classify_line(policy, raw_path, line, line_no=0):
    """Return the first Violation a content line produces, else None."""
    path = normalize_path(raw_path)
    if _allowed(policy, path, line):
        return None
    for rule in policy.content_rules:
        if rule.paths is not None and not rule.paths.search(path):
            continue
        if rule.regex.search(line):
            return Violation(path, line_no, rule.rule_id, rule.title, line.strip()[:160])
    return None


def classify_path(policy, raw_path):
    """Return the Violation a tracked path itself produces, else None."""
    path = normalize_path(raw_path)
    if _allowed(policy, path, None):
        return None
    for rule in policy.path_rules:
        if rule.regex.search(path):
            return Violation(path, 0, rule.rule_id, rule.title, "(forbidden asset path)")
    return None


def classify_blob(policy, raw_path, data):
    """Return the Violation a file's bytes produce by hash, else None."""
    digest = hashlib.sha256(data).hexdigest()
    for rule in policy.blob_rules:
        if digest == rule.sha256:
            return Violation(normalize_path(raw_path), 0, rule.rule_id, rule.title, "sha256=" + digest)
    return None


def scan_file(policy, repo_root, rel_path):
    """Return every Violation one tracked file produces (path, blob, content)."""
    violations = []
    hit = classify_path(policy, rel_path)
    if hit is not None:
        violations.append(hit)
    full = pathlib.Path(repo_root) / rel_path
    try:
        data = full.read_bytes()
    except FileNotFoundError:
        # Tracked but deleted from the working tree: nothing is published from
        # a path with no content, and CI checkouts never hit this.
        return violations
    except OSError as exc:
        raise ProductIdentityError(
            "file-unreadable",
            "Cannot read tracked file {}".format(full),
            hint="Fix permissions; the guard will not guess at file contents.",
            cause=exc,
        )
    hit = classify_blob(policy, rel_path, data)
    if hit is not None:
        violations.append(hit)
    if b"\0" in data[:_BINARY_SNIFF_BYTES]:
        return violations
    text = data.decode("utf-8", errors="replace")
    for line_no, line in enumerate(text.splitlines(), start=1):
        hit = classify_line(policy, rel_path, line, line_no)
        if hit is not None:
            violations.append(hit)
    return violations


def tracked_files(repo_root):
    """Return the repo's tracked paths, the set a push would publish."""
    try:
        result = subprocess.run(
            ["git", "-C", str(repo_root), "ls-files", "-z"],
            capture_output=True,
            check=False,
        )
    except OSError as exc:
        raise ProductIdentityError(
            "git-unavailable",
            "Cannot run git to list tracked files",
            hint="The guard needs a git checkout; install git or use --paths.",
            cause=exc,
        )
    if result.returncode != 0:
        raise ProductIdentityError(
            "git-ls-files-failed",
            "git ls-files failed in {}: {}".format(
                repo_root, result.stderr.decode("utf-8", errors="replace").strip()
            ),
            hint="Run the guard from a git checkout, or pass explicit --paths.",
        )
    return [p.decode("utf-8", errors="replace") for p in result.stdout.split(b"\0") if p]


def self_test(policy, manifest):
    """Execute the manifest's own samples; return human-readable failures.

    must_flag samples must be flagged by exactly their own rule; must_pass and
    clean_samples must not be flagged by any rule. Empty output means the
    policy does what it documents.
    """
    failures = []
    for rule in manifest["identity_rules"]:
        for sample in rule.get("must_flag", []):
            verdict = classify_line(policy, sample["path"], sample["line"])
            if verdict is None:
                failures.append(
                    "{}: expected {!r} to be flagged, but nothing matched".format(rule["id"], sample["line"])
                )
            elif verdict.rule_id != rule["id"]:
                failures.append(
                    "{}: expected {!r} to match {}, but {} matched first".format(
                        rule["id"], sample["line"], rule["id"], verdict.rule_id
                    )
                )
        for sample in rule.get("must_pass", []):
            verdict = classify_line(policy, sample["path"], sample["line"])
            if verdict is not None:
                failures.append(
                    "{}: expected {!r} to stay clean, but {} flagged it".format(
                        rule["id"], sample["line"], verdict.rule_id
                    )
                )
    for rule in manifest.get("forbidden_paths", []):
        for sample in rule.get("must_flag", []):
            verdict = classify_path(policy, sample)
            if verdict is None or verdict.rule_id != rule["id"]:
                failures.append("{}: expected path {!r} to be flagged".format(rule["id"], sample))
        for sample in rule.get("must_pass", []):
            verdict = classify_path(policy, sample)
            if verdict is not None:
                failures.append(
                    "{}: expected path {!r} to stay clean, but {} flagged it".format(
                        rule["id"], sample, verdict.rule_id
                    )
                )
    for sample in manifest.get("clean_samples", []):
        verdict = classify_line(policy, sample["path"], sample["line"])
        if verdict is not None:
            failures.append(
                "clean_samples: {} flagged {!r} ({})".format(
                    verdict.rule_id, sample["line"], sample.get("why", "no rationale recorded")
                )
            )
    return failures


def load_policy(manifest_path):
    """Load, compile, and self-test the policy; fail closed on any defect."""
    manifest = load_manifest(manifest_path)
    policy = compile_policy(manifest)
    failures = self_test(policy, manifest)
    if failures:
        raise ProductIdentityError(
            "manifest-self-test-failed",
            "Identity manifest failed its own samples:\n  " + "\n  ".join(failures),
            hint="The policy does not do what it claims. Fix the rules before trusting any scan.",
        )
    return manifest, policy


def report(violations, source):
    """Print the scan outcome and return the process exit code."""
    if not violations:
        print("product-identity: OK ({})".format(source))
        return EXIT_OK
    print(
        "product-identity: BLOCKED. {} identity violation(s) in {}:".format(len(violations), source),
        file=sys.stderr,
    )
    for item in violations:
        location = "{}:{}".format(item.path, item.line_no) if item.line_no else item.path
        print("  {}\n    {} {}: {}".format(location, item.rule_id, item.title, item.excerpt), file=sys.stderr)
    print(
        "\nThis fork must not present Jellyfin's product identity as its own\n"
        "(https://jellyfin.org/docs/project/branding/). Replace the branding with\n"
        "SlopTank's. Compatibility identifiers and GPL attribution are allowlisted\n"
        "on purpose; if a rule is wrong, change scripts/{} deliberately;\n"
        "do not bypass the check.".format(MANIFEST_NAME),
        file=sys.stderr,
    )
    return EXIT_VIOLATION


def _parse_args(argv):
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("--repo", default=".", help="repository checkout to scan (default: current directory)")
    parser.add_argument(
        "--manifest",
        default=None,
        help="policy manifest path (default: {} next to this script)".format(MANIFEST_NAME),
    )
    parser.add_argument("--self-test", action="store_true", help="only verify the manifest against its own samples")
    parser.add_argument("--paths", nargs="+", metavar="PATH", help="scan these repo-relative paths instead of the git index")
    return parser.parse_args(argv)


def main(argv=None):
    args = _parse_args(sys.argv[1:] if argv is None else argv)
    manifest_path = (
        pathlib.Path(args.manifest)
        if args.manifest
        else pathlib.Path(__file__).resolve().parent / MANIFEST_NAME
    )
    try:
        manifest, policy = load_policy(manifest_path)
        if args.self_test:
            sample_count = sum(
                len(rule.get("must_flag", [])) + len(rule.get("must_pass", []))
                for rule in manifest["identity_rules"] + manifest.get("forbidden_paths", [])
            ) + len(manifest.get("clean_samples", []))
            print(
                "product-identity: manifest self-test OK ({} rules, {} samples)".format(
                    len(manifest["identity_rules"])
                    + len(manifest.get("forbidden_paths", []))
                    + len(manifest.get("forbidden_blobs", [])),
                    sample_count,
                )
            )
            return EXIT_OK
        paths = args.paths if args.paths else tracked_files(args.repo)
        violations = []
        for rel_path in paths:
            violations.extend(scan_file(policy, args.repo, normalize_path(rel_path)))
        source = (
            "{} given path(s)".format(len(paths))
            if args.paths
            else "{} tracked files in {}".format(len(paths), args.repo)
        )
        return report(violations, source)
    except ProductIdentityError as exc:
        print("product-identity: ERROR {}".format(exc), file=sys.stderr)
        print("product-identity: failing closed.", file=sys.stderr)
        return EXIT_CONFIG_ERROR


if __name__ == "__main__":
    sys.exit(main())
