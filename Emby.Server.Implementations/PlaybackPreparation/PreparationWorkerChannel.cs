// SlopTank modification notice: added or changed by SlopTank on 2026-11-03.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using MediaBrowser.Controller.PlaybackPreparation;
using MediaBrowser.Model.PlaybackPreparation;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PlaybackPreparation;

/// <summary>Serves one claimed preparation job over its inherited private pipes.</summary>
internal sealed class PreparationWorkerChannel
{
    private const string Protocol = "org.sloptank.playback-preparation-stdio";
    private const int MaximumMessageBytes = 128 * 1024;
    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IPlaybackPreparationService _service;
    private readonly PreparationMediaWriter _writer;
    private readonly Func<bool> _isIdle;
    private readonly ILogger _logger;
    private readonly Guid _requestId;
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object>>> _actions;
    private PlaybackPreparationRequest _claim;
    private WorkerStage _stage = WorkerStage.Classifying;

    public PreparationWorkerChannel(
        IPlaybackPreparationService service,
        PreparationMediaWriter writer,
        PlaybackPreparationRequest claim,
        Func<bool> isIdle,
        ILogger logger)
    {
        _service = service;
        _writer = writer;
        _claim = claim;
        _requestId = Guid.Parse(claim.RequestId!);
        _isIdle = isIdle;
        _logger = logger;
        _actions = new(StringComparer.Ordinal)
        {
            ["renew"] = RenewAsync,
            ["recheck"] = RecheckAsync,
            ["result"] = SubmitAsync,
            ["idle"] = IdleAsync,
            ["replace"] = ReplaceAsync
        };
    }

    private enum WorkerStage
    {
        Classifying,
        Preparing,
        Finished
    }

    public async Task RunAsync(StreamReader input, StreamWriter output, object runtime, CancellationToken token)
    {
        await WriteAsync(output, new { protocol = Protocol, version = 1, type = "start", request = _claim, runtime }, token).ConfigureAwait(false);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
        startup.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            using var ready = await ReadAsync(input, startup.Token).ConfigureAwait(false);
            if (Text(ready.RootElement, "type") != "ready" || Text(ready.RootElement, "requestId") != _claim.RequestId)
            {
                throw Error("ipc-invalid-ready", "The bundled worker did not acknowledge its assigned request.");
            }
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "The bundled preparation worker did not finish starting for request {RequestId}", _requestId);
            throw Error("worker-start-timeout", "The bundled preparation worker did not start within two minutes.");
        }

        long previousId = 0;
        for (var count = 0; count < 4096; count++)
        {
            using var message = await ReadAsync(input, token).ConfigureAwait(false);
            var frame = message.RootElement;
            if (Text(frame, "type") == "complete")
            {
                if (_stage != WorkerStage.Finished || Text(frame, "requestId") != _claim.RequestId)
                {
                    throw Error("ipc-incomplete-job", "The worker exited before the server accepted a final classification.");
                }

                return;
            }

            if (Text(frame, "type") != "request" || !frame.TryGetProperty("id", out var idField)
                || !idField.TryGetInt64(out var id) || id <= previousId)
            {
                throw Error("ipc-invalid-request", "The worker sent an invalid or repeated RPC identifier.");
            }

            previousId = id;
            try
            {
                var action = Text(frame, "action");
                if (!_actions.TryGetValue(action, out var handler) || !frame.TryGetProperty("payload", out var payload)
                    || payload.ValueKind != JsonValueKind.Object)
                {
                    throw Error("ipc-action-refused", $"Unsupported worker request '{action}'.");
                }

                var result = await handler(payload, token).ConfigureAwait(false);
                await WriteAsync(output, new { protocol = Protocol, version = 1, type = "response", id, ok = true, payload = result, error = (object?)null }, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && !token.IsCancellationRequested)
            {
                var code = exception switch
                {
                    PlaybackPreparationException preparation => preparation.Code,
                    PermalinkException mutation => mutation.Code,
                    _ => exception.GetType().Name
                };
                _logger.LogWarning(exception, "Preparation request {RequestId} RPC failed ({Code})", _requestId, code);
                var detail = exception.Message.Length > 2048 ? exception.Message[..2048] : exception.Message;
                await WriteAsync(
                    output,
                    new
                    {
                        protocol = Protocol,
                        version = 1,
                        type = "response",
                        id,
                        ok = false,
                        payload = (object?)null,
                        error = new
                        {
                            code,
                            message = detail
                        }
                    },
                    token).ConfigureAwait(false);
            }
        }

        throw Error("ipc-request-limit", "The worker exceeded the per-job RPC limit.");
    }

    private async Task<object> RenewAsync(JsonElement payload, CancellationToken token)
    {
        RequireStage(WorkerStage.Preparing);
        RequireEcho(payload);
        await _service.RenewAsync(_requestId, _claim, token).ConfigureAwait(false);
        return new { renewed = true };
    }

    private async Task<object> RecheckAsync(JsonElement payload, CancellationToken token)
    {
        RequireStage(WorkerStage.Preparing);
        RequireEcho(payload);
        await _writer.RefreshMediaInfoIfRequiredAsync(token).ConfigureAwait(false);
        _claim = await _service.RecheckAsync(_requestId, _claim, token).ConfigureAwait(false);
        _stage = WorkerStage.Classifying;
        return new { request = _claim };
    }

    private async Task<object> SubmitAsync(JsonElement payload, CancellationToken token)
    {
        RequireStage(WorkerStage.Classifying);
        var result = Field<PlaybackPreparationResult>(payload, "result");
        await _service.SubmitResultAsync(_requestId, result, token).ConfigureAwait(false);
        _stage = result.Outcome == PlaybackPreparationContract.NeedsPreparationOutcome ? WorkerStage.Preparing : WorkerStage.Finished;
        return new { accepted = true };
    }

    private Task<object> IdleAsync(JsonElement payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<object>(new { idle = _isIdle() });
    }

    private async Task<object> ReplaceAsync(JsonElement payload, CancellationToken token)
    {
        RequireStage(WorkerStage.Preparing);
        var replacement = payload.Deserialize<PreparationMediaWriter.PreparedMediaReplacement>(PayloadOptions)
            ?? throw Error("ipc-invalid-payload", "A replacement object is required.");
        var path = await _writer.ReplaceAsync(replacement, _claim, token).ConfigureAwait(false);
        return new { path };
    }

    private void RequireEcho(JsonElement payload)
    {
        var echoed = Field<PlaybackPreparationRequest>(payload, "request");
        if (!string.Equals(JsonSerializer.Serialize(echoed), JsonSerializer.Serialize(_claim), StringComparison.Ordinal))
        {
            throw Error("ipc-request-binding-mismatch", "The worker changed its current request binding.");
        }
    }

    private void RequireStage(WorkerStage expected)
    {
        if (_stage != expected)
        {
            throw Error("ipc-job-state", $"The job is {_stage}; this operation requires {expected}.");
        }
    }

    private static T Field<T>(JsonElement value, string name)
        where T : class
        => value.TryGetProperty(name, out var field)
            ? field.Deserialize<T>() ?? throw Error("ipc-invalid-payload", $"Field '{name}' must be an object.")
            : throw Error("ipc-invalid-payload", $"Field '{name}' is required.");

    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
        ? field.GetString()! : throw Error("ipc-invalid-frame", $"Field '{name}' must be a string.");

    private static async Task<JsonDocument> ReadAsync(StreamReader input, CancellationToken token)
    {
        var line = new StringBuilder();
        var buffer = new char[1];
        while (await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) == 1)
        {
            if (buffer[0] == '\n')
            {
                break;
            }

            if (line.Length >= MaximumMessageBytes)
            {
                throw Error("ipc-message-too-large", "The worker exceeded its message size limit.");
            }

            line.Append(buffer[0]);
        }

        if (buffer[0] != '\n' || Encoding.UTF8.GetByteCount(line.ToString()) > MaximumMessageBytes)
        {
            throw Error("ipc-unexpected-eof", "The worker closed its pipe or sent an oversized unfinished message.");
        }

        var document = JsonDocument.Parse(line.ToString());
        try
        {
            var frame = document.RootElement;
            if (frame.ValueKind != JsonValueKind.Object || Text(frame, "protocol") != Protocol
                || !frame.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1)
            {
                throw Error("ipc-unknown-protocol", "The bundled preparation worker uses an unsupported protocol.");
            }

            if (Text(frame, "type") == "fatal")
            {
                var error = frame.GetProperty("error");
                throw Error(Text(error, "code"), Text(error, "message"));
            }

            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static async Task WriteAsync(StreamWriter output, object value, CancellationToken token)
    {
        var line = JsonSerializer.Serialize(value);
        if (Encoding.UTF8.GetByteCount(line) + 1 > MaximumMessageBytes)
        {
            throw Error("ipc-message-too-large", "The server response exceeds the preparation protocol's limit.");
        }

        await output.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private static PlaybackPreparationException Error(string code, string message) => new(409, code, message);
}
