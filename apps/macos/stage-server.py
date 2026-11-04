#!/usr/bin/env python3
# SlopTank modification notice: added or changed by SlopTank on 2026-11-04.
"""Stage a completed self-contained server publish and selected SVG converter.

The publish supplies its native server executable and private .NET runtime.
This step copies completed inputs only; it does not install a system runtime,
build the server, or qualify the resulting application.
"""

from __future__ import annotations

import argparse
import importlib.util
import shutil
import sys
from pathlib import Path


def stage(arguments: argparse.Namespace) -> Path:
    """Copy validated self-contained publish files into a new server directory."""
    spec = importlib.util.spec_from_file_location("sloptank_assembler", Path(__file__).with_name("assemble-app.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    build = module._directory(arguments.server_build, "completed self-contained server publish")
    svg = module._directory(arguments.svg_runtime, "SVG converter runtime")

    runtime_assets = (
        ("jellyfin", "native server executable", True),
        ("jellyfin.dll", "server assembly", False),
        ("jellyfin.runtimeconfig.json", "server runtime configuration", False),
        ("jellyfin.deps.json", "server dependency manifest", False),
        ("libcoreclr.dylib", ".NET CoreCLR runtime", False),
        ("libhostpolicy.dylib", ".NET host policy runtime", False),
        ("libhostfxr.dylib", ".NET host resolver", False),
        ("System.Private.CoreLib.dll", ".NET base class library", False),
        ("licenses/Microsoft.NETCore.App.Runtime.osx-x64/LICENSE.TXT", ".NET Core runtime licence notice", False),
        ("licenses/Microsoft.NETCore.App.Runtime.osx-x64/THIRD-PARTY-NOTICES.TXT", ".NET Core third-party notices", False),
        ("licenses/Microsoft.AspNetCore.App.Runtime.osx-x64/LICENSE.txt", "ASP.NET Core runtime licence notice", False),
        ("licenses/Microsoft.AspNetCore.App.Runtime.osx-x64/THIRD-PARTY-NOTICES.TXT", "ASP.NET Core third-party notices", False),
    )
    for filename, label, executable in runtime_assets:
        path = build / filename
        try:
            module._regular_file(path, label, executable=executable)
        except module.AssemblyError as error:
            if error.code != "input_file_unavailable":
                raise
            raise module.AssemblyError(
                "runtime_asset_missing",
                f"The self-contained publish is missing required asset {filename}",
                path,
                error,
            ) from error

    module._regular_file(svg / "svg-rasterizer", "SVG converter", executable=True)
    sources = (build, svg)
    for source in sources:
        if (source / ".assembly-incomplete").exists():
            raise module.AssemblyError("component_incomplete", "An input has an incomplete assembly marker", source)
        module._validate_tree_links(source, source.name)

    output = arguments.output.expanduser().absolute()
    resolved_output = output.resolve()
    if any(module._is_within(resolved_output, source) or module._is_within(source, resolved_output) for source in sources):
        raise module.AssemblyError("output_overlaps_input", "Staged server must be separate from its inputs", output)
    if output.exists() or output.is_symlink():
        raise module.AssemblyError("output_already_exists", "Choose a new server staging directory", output)

    output.parent.mkdir(parents=True, exist_ok=True)
    marker = module._reserve_output(output)
    for source in build.iterdir():
        if source.name == "svg-rasterizer":
            continue  # Install the explicitly selected standalone release below.
        target = output / source.name
        if source.is_dir():
            module._copy_tree(source, target, source.name)
        else:
            shutil.copy2(source, target)
    module._copy_tree(svg, output / "svg-rasterizer", "SVG converter")
    marker.unlink()
    return output


def main() -> int:
    """Parse completed publish inputs and report staging failures."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-build", type=Path, required=True, help="Self-contained dotnet publish output")
    parser.add_argument("--svg-runtime", type=Path, required=True, help="Completed standalone SVG converter runtime")
    parser.add_argument("--output", type=Path, required=True, help="New isolated server staging directory")
    arguments = parser.parse_args()
    try:
        print(stage(arguments))
    except Exception as error:
        print(f"Server staging failed: {type(error).__name__}: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
