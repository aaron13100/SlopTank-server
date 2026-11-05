<!-- SlopTank modification notice: added or changed by SlopTank on 2026-11-04, 2026-11-05. -->
# SlopTank for macOS

SlopTank uses one Mac download for Intel and Apple chips (M1 or later), targeting
macOS 14 or later. This matches [.NET 10's supported macOS range](https://learn.microsoft.com/en-us/dotnet/core/install/macos#supported-versions).
The Swift launcher targets macOS 12. The universal candidate also passed its
installed-app smoke on an Intel Mac running macOS 12.7.6. That is a tested
configuration; the vendor-supported baseline remains macOS 14. Native Apple-chip
and macOS 15 execution must pass the GitHub workflow before a public release.
Local candidates are not public downloads.

Install by opening the DMG, dragging SlopTank to Applications, opening it and
choosing Start SlopTank. Browser setup creates the administrator and library.
The app includes its server, web client, FFmpeg, SVG converter, Intro Skipper and
preparation runtime. Customers install no separate .NET or Python runtime.

The universal Swift launcher selects one runtime through `MacOSRuntime.swift`.
The complete .NET/SVG and Python trees remain separate. Static FFmpeg and
ffprobe are merged with `lipo`; architecture-neutral content is stored once:

```text
SlopTank.app/Contents/
  MacOS/SlopTank                         # x86_64 + arm64
  Resources/
    Runtimes/osx-x64/{Server,Preparation}
    Runtimes/osx-arm64/{Server,Preparation}
    Web/
    ffmpeg/{ffmpeg,ffprobe}              # x86_64 + arm64
    IntroSkipper/
  SharedSupport/ReleaseMaterials/
```

Each Server includes its standalone SVG converter. The launcher supplies the shared
plugin directory through `JELLYFIN_BundledIntroSkipper__Path`. The server's normal plugin
loader copies Intro Skipper 12.0.4.0 into a new profile, enabling automatic
new-media analysis and settled-season reanalysis. Existing plugins/settings
are retained. EF Core and SQLite pins match the official plugin's 10.0.11 ABI.

Build from the public SlopTank-server repository containing this directory and
`apps/preparation/playback-preparation.spec` with its pipeline modules. Use Xcode command
line tools, .NET 10 and Python 3.12. On each matching native Mac, create a fresh
scratch directory and Python venv, install PyInstaller 6.22.3, guessit 3.8.0 and
charset-normalizer 3.4.4, then run:

```sh
python3 apps/macos/build-native.py --rid osx-x64 --output build/osx-x64
# On the Apple-chip Mac, use --rid osx-arm64 and its own new output directory.
```

`build-native.py` uses self-contained `dotnet publish` (including SVG), Swift and
PyInstaller, with fixed command deadlines and normal priority.
`fetch-components.py` verifies the pinned Intro Skipper archive hash. FFmpeg is
built separately from the pinned source by `.github/workflows/ffmpeg-subtitles.yml`;
the universal workflow downloads both architecture artifacts from its fixed run,
checks their source pins and binary hashes, and packages the corresponding-source
kit separately from the app. Runtime-pack licence texts remain under each publish's
`licenses/<NuGetPackageId>/`. The optional `stage-server.py` validates/copies a
completed publish and can select a separately built SVG converter; it never
constructs another .NET runtime.

Merge the two launcher slices and FFmpeg/ffprobe pairs with `lipo -create`, then
ad-hoc sign the merged binaries before local execution. Assemble completed inputs:

```sh
python3 apps/macos/assemble-app.py \
  --launcher-binary build/universal/SlopTank \
  --runtime osx-x64=build/osx-x64 \
  --runtime osx-arm64=build/osx-arm64 \
  --web-dir build/web \
  --ffmpeg-dir build/universal/ffmpeg \
  --intro-skipper-plugin build/IntroSkipper \
  --release-materials-dir build/materials \
  --version 0.1.0 --build-number 1 \
  --output build/SlopTank.app
python3 apps/macos/package-dmg.py --app build/SlopTank.app --output build/SlopTank.dmg
python3 apps/macos/smoke-installed-app.py --dmg build/SlopTank.dmg --work-dir build/smoke --timeout-seconds 1800
```

Assembly refuses an existing output, incomplete inputs, overlapping paths and
escaping component symlinks. It copies prebuilt components rather than qualifying
them; verify the native slices before assembly. Runtime IDs use the portable
Server/Preparation shape, allowing later linux-x64, linux-arm64 and win-x64
packagers to reuse it. The macOS adapter requires both Mac runtime IDs. Preparation is a shared product
program under `apps/preparation`, independent of the native installer. Its
portable defaults use the configured profile root; the parent supplies the
actual paths before any pipeline module is imported.

For a cross-built preparation component, `PYINSTALLER_TARGET_ARCH` passes through
to PyInstaller's standard `EXE(target_arch=...)` option. The Python interpreter
and every native dependency must contain that architecture. Native CI needs no
override. An Intel Mac cannot execute the arm64 worker.

`.github/workflows/macos-universal.yml` in SlopTank-server builds native components
on `macos-15-intel` and `macos-15` (Apple chip), builds the pinned public SlopTank
web source, retrieves the two checked FFmpeg source-build artifacts, assembles one
app/DMG plus its corresponding-source kit, and smokes that same installed image on both
runner types. Push the pinned public web commit first; pushing the server's
`release-universal` branch then starts the workflow automatically. Manual dispatch
is available after the workflow reaches the default branch. The smoke uses an isolated
profile/port and a clip generated with the bundled FFmpeg, then checks setup,
HTTP 206 bytes, installed SVG conversion and preparation-worker startup, normal
Quit, retained-profile relaunch and removal. The worker probe checks its expected
end-of-input response; a separate smoke step converts an incompatible clip and checks
playback bytes and the retained original. The smoke bypasses
the welcome-window interaction using a ready-profile fixture. Outputs are workflow
artifacts only, with no release or tag. Ad-hoc signing permits native execution;
Developer ID signing, notarization and exact-component source/licence clearance
remain requirements for public distribution.

Profiles live outside the app, by default in the user's Application Support
folder. Before first startup an empty data folder may be selected; existing files
are never moved. A private profile identity and matching markers bind the chosen
folder and separate permalink-authority directory. Corrupt configuration,
mismatched markers or inaccessible folders stop startup without creating another
profile. Only the folder bookmark is stored in UserDefaults. After successful
startup the location is fixed; reauthorization retains the same folder.

The launcher chooses an available HTTP port from 8096 to 8196 for a new profile
and preserves saved ports, HTTPS and base URLs. Child temporary files use the
profile's private cache. Startup has a ten-minute load-scaled baseline; HTTP
requests and the outer smoke retain fixed deadlines. The server owns its data
directory through a lifetime .NET file lease and removes a retained SQLite
migration-lock row only under that lease. Normal Quit allows 60 seconds for server
shutdown. Upgrade means Quit, replace the app and reopen it. Removing the app
retains the profile and media; no data-removal command is provided.
