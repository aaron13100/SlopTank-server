<h1 align="center">SlopTank Server</h1>
<h3 align="center">A customized media server based on <a href="https://jellyfin.org">Jellyfin</a></h3>

---

<p align="center">
<img alt="SlopTank" src="Jellyfin.Server/wwwroot/api-docs/sloptank.png"/>
<br/>
<br/>
<a href="https://github.com/aaron13100/SlopTank-server/blob/master/LICENSE">
<img alt="GPL 2.0 License" src="https://img.shields.io/github/license/aaron13100/SlopTank-server.svg"/>
</a>
<a href="https://github.com/aaron13100/SlopTank-server/issues">
<img alt="Issues" src="https://img.shields.io/github/issues/aaron13100/SlopTank-server.svg"/>
</a>
</p>

---

SlopTank Server is a customized media server based on Jellyfin, which puts you in control of managing
and streaming your media to end-user devices via multiple apps. It is an independent fork: it is not
affiliated with, endorsed by, or supported by the Jellyfin project, and it does not use the Jellyfin
name or logo as its own identity.

Jellyfin itself is descended from Emby's 3.5.2 release and ported to the .NET platform to enable full
cross-platform support. For background on the upstream project, see
[jellyfin.org](https://jellyfin.org/docs/).

<strong>Something not working right?</strong><br/>
Open an <a href="https://github.com/aaron13100/SlopTank-server/issues">issue</a> on GitHub.<br/>

Translations of the server's user-visible strings are inherited from upstream Jellyfin, whose
translation work is managed on the <a href="https://translate.jellyfin.org">Jellyfin Weblate</a>
instance. SlopTank-specific wording is edited directly in
`Emby.Server.Implementations/Localization/Core/`.

---

## SlopTank Server

This repository contains the code for SlopTank's backend server. The matching web client lives in
[aaron13100/SlopTank](https://github.com/aaron13100/SlopTank).

## Server Development

These instructions will help you get set up with a local development environment in order to contribute to this repository. Note that this project is supported on all major operating systems except FreeBSD, which is still incompatible.

### Prerequisites

Before the project can be built, you must first install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet) on your system.

Instructions to run this project from the command line are included here, but you will also need to install an IDE if you want to debug the server while it is running. Any IDE that supports .NET 6 development will work, but two options are recent versions of [Visual Studio](https://visualstudio.microsoft.com/downloads/) (at least 2022) and [Visual Studio Code](https://code.visualstudio.com/Download).

[ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) will also need to be installed.

### Cloning the Repository

After dependencies have been installed you will need to clone a local copy of this repository. The following example shows how you can clone the repository directly over HTTPS.

```bash
git clone https://github.com/aaron13100/SlopTank-server.git
```

### Installing the Web Client

The server is configured to host the static files required for the [web client](https://github.com/aaron13100/SlopTank) in addition to serving the backend by default. Before you can run the server, you will need to get a copy of the web client since they are not included in this repository directly.

Note that it is recommended for development to [host the web client separately](#hosting-the-web-client-separately) from the web server with some additional configuration, in which case you can skip this step.

There are two options to get the files for the web client.

1. Build them from source following the instructions on the [SlopTank repository](https://github.com/aaron13100/SlopTank)
2. Get the pre-built files from an existing installation of the server.

### Running The Server

The following instructions will help you get the project up and running via the command line, or your preferred IDE.

#### Running With Visual Studio

To run the project with Visual Studio you can open the Solution (`.sln`) file and then press `F5` to run the server.

#### Running With Visual Studio Code

To run the project with Visual Studio Code you will first need to open the repository directory with Visual Studio Code using the `Open Folder...` option.

Second, you need to [install the recommended extensions for the workspace](https://code.visualstudio.com/docs/editor/extension-gallery#_recommended-extensions). Note that extension recommendations are classified as either "Workspace Recommendations" or "Other Recommendations", but only the "Workspace Recommendations" are required.

After the required extensions are installed, you can run the server by pressing `F5`.

#### Running From the Command Line

To run the server from the command line you can use the `dotnet run` command. The example below shows how to do this if you have cloned the repository into a directory named `SlopTank-server` and should work on all operating systems.

```bash
cd SlopTank-server                   # Move into the repository directory
dotnet run --project Jellyfin.Server --webdir /absolute/path/to/sloptank-web/dist # Run the server startup project
```

A second option is to build the project and then run the resulting executable file directly. When running the executable directly you can easily add command line options. Add the `--help` flag to list details on all the supported command line options.

1. Build the project

```bash
dotnet build                       # Build the project
cd Jellyfin.Server/bin/Debug/net10.0 # Change into the build output directory
```

2. Execute the build output. On Linux, Mac, etc. use `./jellyfin` and on Windows use `jellyfin.exe`.

> [!NOTE]
> The executable, the project directories, and the .NET namespaces are still named `jellyfin` /
> `Jellyfin.*` / `MediaBrowser.*` / `Emby.*` on purpose. Every third-party server plugin binds to
> those assembly names, and the data directory, database file, and `JELLYFIN_*` environment
> variables are the identity of already-deployed installations. Renaming them would break running
> software rather than remove a trademark, so only user-facing product naming is rebranded.

#### Accessing the Hosted Web Client

If the Server is configured to host the Web Client, and the Server is running, the Web Client can be accessed at `http://localhost:8096` by default.

API documentation can be viewed at `http://localhost:8096/api-docs/swagger/index.html`

### Running from GitHub Codespaces

The server can also be run in a container on a GitHub hosted server. `.devcontainer/` holds the
configuration; `.devcontainer/install-ffmpeg.sh` installs ffmpeg into the container.

**NOTE:** it might take 20-30 seconds to load all extensions and prepare the environment while VS Code is already open. Just give it some time and wait until the .NET download reports `Done!` in the output tab.

**NOTE:** If you want to access the instance from outside, like with a web client on another PC, remember to set the "ports" in the lower VS Code window to public.

**NOTE:** When first opening the server instance with any web UI, you will be sent to the login instead of the setup page. Refresh the login page once and you should be redirected to the Setup.

The container has no web client preloaded, so use the `.NET Launch (nowebclient)` launch config to start the server and connect an external client.

### Running The Tests

The regression suite is maintained outside this public product repository. Pull requests run contributor-safe public checks that restore dependencies, verify formatting, build and type-check the server, and exercise the command-line entry point. A separate maintainer-approved check runs the private regression suite against the exact pull request commit and reports a stable public requirement ID with a sanitized failure reason.

Tests submitted in a pull request cannot be accepted into this repository. Describe the behavior the change needs to cover in the pull request; maintainers will add the corresponding regression coverage to the private tier.

### Advanced Configuration

The following sections describe some more advanced scenarios for running the server from source that build upon the standard instructions above.

#### Hosting The Web Client Separately

It is not necessary to host the frontend web client as part of the backend server. Hosting these two components separately may be useful for frontend developers who would prefer to host the client in a separate webpack development server for a tighter development loop. See the [SlopTank](https://github.com/aaron13100/SlopTank#build-process) repo for instructions on how to do this.

To instruct the server not to host the web content, there is a `nowebclient` configuration flag that must be set. This can be specified using the command line
switch `--nowebclient` or the environment variable `JELLYFIN_NOWEBCONTENT=true`.

Since this is a common scenario, there is also a separate launch profile defined for Visual Studio called `Jellyfin.Server (nowebcontent)` that can be selected from the 'Start Debugging' dropdown in the main toolbar.

**NOTE:** The setup wizard cannot be run if the web client is hosted separately.
