# Build-only recipe for the self-contained preparation helper.
# PyInstaller supplies Analysis, PYZ, EXE, COLLECT and SPEC in this context.
# https://pyinstaller.org/en/stable/spec-files.html
from importlib.metadata import version
import os
from pathlib import Path

from PyInstaller.utils.hooks import collect_data_files, copy_metadata


tools = Path(SPEC).resolve().parent
for distribution in ("guessit", "charset-normalizer"):
    version(distribution)

# Include the existing pipeline's lazy local imports without importing the
# application or reading its configuration during recipe evaluation.
modules = ["lib." + source.stem for source in (tools / "lib").glob("*.py")
           if source.stem != "__init__"]
data = collect_data_files("guessit") + collect_data_files("rebulk")
data += copy_metadata("guessit", recursive=True) + copy_metadata("charset-normalizer", recursive=True)
analysis = Analysis(
    [str(tools / "playback-preparation-stdio.py")],
    pathex=[str(tools)],
    binaries=[],
    datas=data,
    hiddenimports=sorted(modules + ["guessit", "charset_normalizer"]),
    noarchive=False,
)
archive = PYZ(analysis.pure)
executable = EXE(
    archive,
    analysis.scripts,
    [],
    exclude_binaries=True,
    name="sloptank-prepare",
    console=True,
    strip=False,
    upx=False,
    target_arch=os.environ.get("PYINSTALLER_TARGET_ARCH"),
)
runtime = COLLECT(
    executable,
    analysis.binaries,
    analysis.datas,
    name="Preparation",
    strip=False,
    upx=False,
)
