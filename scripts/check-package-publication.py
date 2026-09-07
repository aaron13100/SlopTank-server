#!/usr/bin/env python3
"""Fail closed if this fork starts publishing internal server NuGet packages."""

import pathlib
import sys
import xml.etree.ElementTree as ET


REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent
CENTRAL_PROPS = REPO_ROOT / "Directory.Build.props"
FORBIDDEN_PACKAGE_FIELDS = {
    "AllowedOutputExtensionsInPackageBuildOutputFolder",
    "Authors",
    "EmbedUntrackedSources",
    "IncludeSymbols",
    "PackageId",
    "PackageLicenseExpression",
    "PublishRepositoryUrl",
    "RepositoryUrl",
    "SymbolPackageFormat",
}


def _local_name(tag):
    return tag.rsplit("}", 1)[-1]


def _parse(path):
    try:
        return ET.parse(path).getroot()
    except (OSError, ET.ParseError) as exc:
        raise RuntimeError("cannot parse {}: {}".format(path.relative_to(REPO_ROOT), exc)) from exc


def check_repository():
    violations = []
    root = _parse(CENTRAL_PROPS)
    central_values = []
    for group in root:
        if _local_name(group.tag) != "PropertyGroup" or group.attrib.get("Condition"):
            continue
        for element in group:
            if _local_name(element.tag) == "IsPackable":
                central_values.append((element.text or "").strip().lower())
    if central_values != ["false"]:
        violations.append(
            "Directory.Build.props must declare exactly one unconditional <IsPackable>false</IsPackable>"
        )

    for project in sorted(REPO_ROOT.rglob("*.csproj")):
        if any(part in {"bin", "obj"} for part in project.parts):
            continue
        project_root = _parse(project)
        relative = project.relative_to(REPO_ROOT)
        for element in project_root.iter():
            field = _local_name(element.tag)
            value = (element.text or "").strip()
            if field == "IsPackable" and value.lower() != "false":
                violations.append("{}: IsPackable must not override the central false value".format(relative))
            if field in FORBIDDEN_PACKAGE_FIELDS:
                violations.append(
                    "{}: stale NuGet publication field <{}>{}</{}> is forbidden".format(
                        relative, field, value, field
                    )
                )
    return violations


def main():
    try:
        violations = check_repository()
    except RuntimeError as exc:
        print("package-publication: ERROR: {}".format(exc), file=sys.stderr)
        print("package-publication: failing closed", file=sys.stderr)
        return 2
    if violations:
        print("package-publication: BLOCKED", file=sys.stderr)
        for violation in violations:
            print("- {}".format(violation), file=sys.stderr)
        return 1
    print("package-publication: OK (all server projects are non-packable)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
