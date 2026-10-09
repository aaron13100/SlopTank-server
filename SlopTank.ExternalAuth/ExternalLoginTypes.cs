// SlopTank modification notice: added or changed by SlopTank on 2026-10-08, 2026-10-09.

namespace SlopTank.ExternalAuth;

/// <summary>
/// The flow mode a challenge is issued for. Login flows are anonymous
/// browser logins; link flows were explicitly initiated from an
/// authenticated local session whose reference is retained server-side and
/// re-resolved at completion.
/// </summary>
public enum ExternalLoginFlowMode
{
    /// <summary>Ordinary anonymous browser login.</summary>
    Login = 0,

    /// <summary>Explicit link of an external identity to an authenticated
    /// local account.</summary>
    Link = 1,
}

/// <summary>
/// Client identity fields captured from the initiating page. These are the
/// same fields every stock request already carries in its authorization
/// header; they are display/bookkeeping data only and never authorize
/// anything or bound any store.
/// </summary>
public sealed record ExternalLoginClient(
    string? AppName,
    string? AppVersion,
    string? DeviceId,
    string? DeviceName);

/// <summary>
/// The context handed to a provider when a challenge is requested. The
/// correlation secret is a one-use random value bound to this single flow:
/// the provider must persist only a hash of it and must never place it in a
/// URL, log, or result.
/// </summary>
public sealed record ExternalLoginContext(
    string FlowReference,
    ExternalLoginFlowMode Mode,
    [property: System.Text.Json.Serialization.JsonIgnore] string CorrelationSecret,
    ExternalLoginClient? Client,
    string? ReturnUrl)
{
    /// <summary>Renders flow context without its browser-binding secret.</summary>
    public override string ToString() =>
        $"{nameof(ExternalLoginContext)} {{ FlowReference = [REDACTED], Mode = {Mode}, CorrelationSecret = [REDACTED] }}";
}

/// <summary>
/// Typed challenge outcome: a redirect challenge on success, or a typed
/// sanitized error. Providers never signal flow failures by throwing;
/// caller cancellation still propagates as
/// <see cref="OperationCanceledException"/>.
/// </summary>
public sealed record ExternalLoginChallengeResult
{
    private ExternalLoginChallengeResult(
        bool isSuccess,
        string? redirectUrl,
        ExternalAuthErrorCode errorCode,
        string? errorDetail)
    {
        IsSuccess = isSuccess;
        RedirectUrl = redirectUrl;
        ErrorCode = errorCode;
        ErrorDetail = errorDetail;
    }

    /// <summary>Gets a value indicating whether this outcome carries a
    /// usable challenge redirect.</summary>
    public bool IsSuccess { get; }

    /// <summary>Gets the absolute redirect URL on success; null
    /// otherwise.</summary>
    public string? RedirectUrl { get; }

    /// <summary>Gets the typed error code; <see cref="ExternalAuthErrorCode.None"/>
    /// on success.</summary>
    public ExternalAuthErrorCode ErrorCode { get; }

    /// <summary>Gets a sanitized short detail token for diagnostics; never
    /// carries credentials, tokens or provider response bodies.</summary>
    public string? ErrorDetail { get; }

    /// <summary>Builds a successful challenge redirecting the browser to
    /// the provider.</summary>
    /// <param name="redirectUrl">The absolute redirect URL.</param>
    /// <returns>The successful outcome.</returns>
    public static ExternalLoginChallengeResult Succeed(string redirectUrl) =>
        new(true, redirectUrl, ExternalAuthErrorCode.None, null);

    /// <summary>Builds a typed challenge failure.</summary>
    /// <param name="code">The sanitized error code.</param>
    /// <param name="detail">Optional sanitized detail token.</param>
    /// <returns>The failed outcome.</returns>
    public static ExternalLoginChallengeResult Fail(ExternalAuthErrorCode code, string? detail = null) =>
        new(false, null, code, detail);
}
