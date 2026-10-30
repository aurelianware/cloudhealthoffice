using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Edi.Core;

/// <summary>An authenticated, validated real-time request handed to a payload handler.</summary>
public sealed class CoreRealTimeRequest
{
    public required CoreSubmitter Submitter { get; init; }
    public required string PayloadType { get; init; }
    public required string PayloadId { get; init; }
    public required string ReceiverId { get; init; }
    public required string Payload { get; init; }
    public string TenantId => Submitter.TenantId;
}

/// <summary>What a handler answers with: a 271/277, or a 999/TA1 rejecting the request.</summary>
public sealed record CorePayloadResult(string PayloadType, string Payload, string Outcome)
{
    public static class Outcomes
    {
        public const string Answered = "answered";
        public const string Rejected999 = "rejected_999";
        public const string RejectedTa1 = "rejected_ta1";
        public const string UnableToRespond = "unable_to_respond";
    }
}

/// <summary>
/// Thrown by a handler when the payload is not X12 it can acknowledge at all
/// (no ISA for a TA1, no GS for a 999). The envelope answers with
/// <c>PayloadIllegal</c>.
/// </summary>
public sealed class CorePayloadUnreadableException : Exception
{
    public CorePayloadUnreadableException(string message) : base(message) { }
}

/// <summary>
/// Answers one CORE request payload type (a 270, a 276) for the service that
/// owns the data. The envelope, authentication, timing and audit are the
/// processor's; a handler sees X12 in and returns X12 out.
/// </summary>
public interface ICoreRealTimeHandler
{
    /// <summary>The request PayloadType this handler answers (e.g. <see cref="CorePayloadTypes.Eligibility270"/>).</summary>
    string RequestPayloadType { get; }

    Task<CorePayloadResult> HandleAsync(CoreRealTimeRequest request, CancellationToken ct);

    /// <summary>
    /// The response sent when the handler has not answered within the
    /// response budget (a 271 with AAA 42, a 277 with STC E1), built from the
    /// request alone. Null when the request cannot be read well enough to
    /// build one; the envelope then carries a Receiver error.
    /// </summary>
    CorePayloadResult? UnableToRespond(CoreRealTimeRequest request);
}

/// <summary>
/// Runs one CORE Connectivity vC2.2.0 real-time exchange: reads the SOAP or
/// MIME envelope, checks its fields, authenticates the submitter, runs the
/// payload handler inside the response budget, and writes the response
/// envelope in the format the request came in. Every exchange is logged once
/// (no payload content) and measured for the CORE response-time rule.
/// </summary>
public sealed class CoreRealTimeProcessor
{
    private readonly IReadOnlyDictionary<string, ICoreRealTimeHandler> _handlers;
    private readonly ICoreSubmitterAuthenticator _authenticator;
    private readonly IOptionsMonitor<CoreConnectivityOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CoreRealTimeProcessor> _logger;

    public CoreRealTimeProcessor(
        IEnumerable<ICoreRealTimeHandler> handlers,
        ICoreSubmitterAuthenticator authenticator,
        IOptionsMonitor<CoreConnectivityOptions> options,
        ILogger<CoreRealTimeProcessor> logger,
        TimeProvider? time = null)
    {
        _handlers = handlers.ToDictionary(h => h.RequestPayloadType, StringComparer.Ordinal);
        _authenticator = authenticator;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyCollection<string> RequestPayloadTypes => _handlers.Keys.ToList();

    /// <summary>Handles the HTTP exchange and writes the response.</summary>
    public async Task ProcessAsync(HttpContext http, string requiredPermission)
    {
        var started = Stopwatch.GetTimestamp();
        var ct = http.RequestAborted;
        var options = _options.CurrentValue;

        CoreEnvelopeRequest? envelope = null;
        CoreEnvelopeResponse response;
        CoreSubmitter? submitter = null;
        var outcome = "envelope_error";

        if (http.Request.ContentLength > options.MaxRequestBytes)
        {
            await WriteRawAsync(http, StatusCodes.Status413PayloadTooLarge, "text/plain", "Request too large.");
            Record(null, null, null, StatusCodes.Status413PayloadTooLarge, "too_large", started);
            return;
        }

        var format = FormatOf(http.Request.ContentType);
        if (format is null)
        {
            await WriteRawAsync(http, StatusCodes.Status415UnsupportedMediaType, "text/plain",
                "Send a SOAP 1.2 (application/soap+xml) or MIME multipart (multipart/form-data) CORE envelope.");
            Record(null, null, null, StatusCodes.Status415UnsupportedMediaType, "unsupported_media", started);
            return;
        }

        try
        {
            envelope = await ReadAsync(http, format.Value, options.MaxRequestBytes, ct);
        }
        catch (CoreEnvelopeFormatException ex)
        {
            if (format == CoreEnvelopeFormat.Soap)
                await WriteRawAsync(http, StatusCodes.Status400BadRequest, CoreSoapCodec.ContentType, CoreSoapCodec.WriteFault(ex.Message));
            else
                await WriteRawAsync(http, StatusCodes.Status400BadRequest, "text/plain", ex.Message);
            Record(null, null, null, StatusCodes.Status400BadRequest, "unreadable", started);
            return;
        }

        var error = Validate(envelope, options);
        if (error is not null)
        {
            response = Error(envelope, StatusCodes.Status400BadRequest, error.Value.Code, error.Value.Message);
        }
        else
        {
            submitter = _authenticator.Authenticate(http, envelope, requiredPermission, out var failure);
            if (submitter is null)
            {
                _logger.LogWarning("CORE request not authenticated: {Reason}. SenderID={SenderId} PayloadID={PayloadId}",
                    failure?.Reason, LogSanitizer.Sanitize(envelope.SenderId), LogSanitizer.Sanitize(envelope.PayloadId));
                response = Error(envelope, StatusCodes.Status401Unauthorized, CoreErrorCodes.Unauthorized, "The credential was not accepted.");
            }
            else if (submitter.PayloadTypes.Count > 0 && !submitter.PayloadTypes.Contains(envelope.PayloadType!, StringComparer.Ordinal))
            {
                response = Error(envelope, StatusCodes.Status403Forbidden, CoreErrorCodes.Unauthorized,
                    $"SenderID is not registered for {envelope.PayloadType}.");
            }
            else
            {
                (response, outcome) = await RunHandlerAsync(envelope, submitter, options, ct);
            }
        }

        await WriteAsync(http, response);
        Record(envelope, response, submitter, response.HttpStatus, outcome, started);
    }

    private async Task<(CoreEnvelopeResponse, string)> RunHandlerAsync(
        CoreEnvelopeRequest envelope, CoreSubmitter submitter, CoreConnectivityOptions options, CancellationToken ct)
    {
        var handler = _handlers[envelope.PayloadType!];
        var request = new CoreRealTimeRequest
        {
            Submitter = submitter,
            PayloadType = envelope.PayloadType!,
            PayloadId = envelope.PayloadId!,
            ReceiverId = envelope.ReceiverId!,
            Payload = envelope.Payload!,
        };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.ResponseBudget);

        CorePayloadResult? result;
        try
        {
            // WhenAny as well as the token: a handler stuck in a call that
            // ignores cancellation still cannot hold the response past the budget.
            var work = handler.HandleAsync(request, budget.Token);
            var finished = await Task.WhenAny(work, Task.Delay(options.ResponseBudget, _time, ct));
            result = finished == work ? await work : null;
            if (result is null)
            {
                budget.Cancel();
                _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = null;
        }
        catch (CorePayloadUnreadableException ex)
        {
            return (Error(envelope, StatusCodes.Status400BadRequest, CoreErrorCodes.Illegal(CoreFields.Payload), ex.Message), "payload_unreadable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "CORE handler failed. PayloadType={PayloadType} PayloadID={PayloadId}",
                envelope.PayloadType, LogSanitizer.Sanitize(envelope.PayloadId));
            result = handler.UnableToRespond(request);
            return result is null
                ? (Error(envelope, StatusCodes.Status500InternalServerError, CoreErrorCodes.ReceiverError, "The receiver could not process the request."), "handler_error")
                : (Success(envelope, result), CorePayloadResult.Outcomes.UnableToRespond);
        }

        if (result is null)
        {
            _logger.LogWarning("CORE handler exceeded the response budget ({Budget}). PayloadType={PayloadType} PayloadID={PayloadId}",
                options.ResponseBudget, envelope.PayloadType, LogSanitizer.Sanitize(envelope.PayloadId));
            result = handler.UnableToRespond(request);
            return result is null
                ? (Error(envelope, StatusCodes.Status500InternalServerError, CoreErrorCodes.ReceiverError, "The receiver could not respond in time."), "timeout")
                : (Success(envelope, result), CorePayloadResult.Outcomes.UnableToRespond);
        }

        return (Success(envelope, result), result.Outcome);
    }

    internal (string Code, string Message)? Validate(CoreEnvelopeRequest e, CoreConnectivityOptions options)
    {
        foreach (var (field, value) in new[]
                 {
                     (CoreFields.PayloadType, e.PayloadType),
                     (CoreFields.ProcessingMode, e.ProcessingMode),
                     (CoreFields.PayloadId, e.PayloadId),
                     (CoreFields.TimeStamp, e.TimeStamp),
                     (CoreFields.SenderId, e.SenderId),
                     (CoreFields.ReceiverId, e.ReceiverId),
                     (CoreFields.CoreRuleVersion, e.CoreRuleVersion),
                 })
        {
            if (string.IsNullOrEmpty(value))
                return (CoreErrorCodes.Required(field), $"{field} is required.");
        }

        if (e.CoreRuleVersion != CoreRule.Version)
            return (CoreErrorCodes.VersionMismatch, $"CORERuleVersion {CoreRule.Version} is supported.");
        if (e.ProcessingMode != "RealTime")
            return (CoreErrorCodes.Illegal(CoreFields.ProcessingMode), "This endpoint accepts ProcessingMode RealTime only.");
        if (!_handlers.ContainsKey(e.PayloadType!))
            return (CoreErrorCodes.Illegal(CoreFields.PayloadType), $"This endpoint accepts {string.Join(", ", _handlers.Keys)}.");
        if (!Guid.TryParse(e.PayloadId, out _))
            return (CoreErrorCodes.Illegal(CoreFields.PayloadId), "PayloadID must be a UUID (RFC 4122).");
        if (!DateTimeOffset.TryParse(e.TimeStamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            return (CoreErrorCodes.Illegal(CoreFields.TimeStamp), "TimeStamp must be an XML Schema dateTime.");
        if (options.ReceiverIds.Count > 0 && !options.ReceiverIds.Contains(e.ReceiverId!, StringComparer.Ordinal))
            return (CoreErrorCodes.Illegal(CoreFields.ReceiverId), "ReceiverID is not served here.");
        if (string.IsNullOrEmpty(e.Payload))
            return (CoreErrorCodes.Required(CoreFields.Payload), "Payload is required.");
        if (e.PayloadLength is not null
            && (!int.TryParse(e.PayloadLength, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length != Encoding.UTF8.GetByteCount(e.Payload)))
            return (CoreErrorCodes.PayloadLengthMismatch, "PayloadLength does not match the payload.");
        if (e.Checksum is not null
            && !string.Equals(e.Checksum, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(e.Payload))), StringComparison.OrdinalIgnoreCase))
            return (CoreErrorCodes.ChecksumMismatch, "Checksum (SHA-1) does not match the payload.");

        return null;
    }

    private CoreEnvelopeResponse Success(CoreEnvelopeRequest e, CorePayloadResult result) => new()
    {
        Format = e.Format,
        PayloadType = result.PayloadType,
        PayloadId = e.PayloadId,
        TimeStamp = Now(),
        SenderId = e.ReceiverId,
        ReceiverId = e.SenderId,
        Payload = result.Payload,
    };

    private CoreEnvelopeResponse Error(CoreEnvelopeRequest e, int status, string code, string message) => new()
    {
        Format = e.Format,
        PayloadType = e.PayloadType,
        PayloadId = e.PayloadId,
        TimeStamp = Now(),
        SenderId = e.ReceiverId,
        ReceiverId = e.SenderId,
        ErrorCode = code,
        ErrorMessage = message,
        HttpStatus = status,
    };

    private string Now() => _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    internal static CoreEnvelopeFormat? FormatOf(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return null;
        if (contentType.StartsWith(CoreSoapCodec.ContentType, StringComparison.OrdinalIgnoreCase)) return CoreEnvelopeFormat.Soap;
        if (contentType.StartsWith(CoreMimeCodec.ContentType, StringComparison.OrdinalIgnoreCase)) return CoreEnvelopeFormat.Mime;
        return null;
    }

    private static async Task<CoreEnvelopeRequest> ReadAsync(HttpContext http, CoreEnvelopeFormat format, long maxBytes, CancellationToken ct)
    {
        if (format == CoreEnvelopeFormat.Mime)
        {
            IFormCollection form;
            try
            {
                form = await http.Request.ReadFormAsync(new Microsoft.AspNetCore.Http.Features.FormOptions
                {
                    MultipartBodyLengthLimit = maxBytes,
                    ValueLengthLimit = (int)Math.Min(maxBytes, int.MaxValue),
                }, ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException)
            {
                throw new CoreEnvelopeFormatException("The request is not a readable multipart/form-data body.", ex);
            }
            return await CoreMimeCodec.ParseAsync(form, ct);
        }

        var buffer = new char[8192];
        var sb = new StringBuilder();
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            sb.Append(buffer, 0, read);
            if (sb.Length > maxBytes)
                throw new CoreEnvelopeFormatException("The request is too large.");
        }
        return CoreSoapCodec.Parse(sb.ToString());
    }

    private static Task WriteAsync(HttpContext http, CoreEnvelopeResponse response)
    {
        if (response.Format == CoreEnvelopeFormat.Mime)
        {
            var (body, contentType) = CoreMimeCodec.Write(response);
            return WriteRawAsync(http, response.HttpStatus, contentType, body);
        }
        return WriteRawAsync(http, response.HttpStatus, CoreSoapCodec.ContentType + "; charset=utf-8", CoreSoapCodec.Write(response));
    }

    private static async Task WriteRawAsync(HttpContext http, int status, string contentType, string body)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = contentType;
        http.Response.Headers.CacheControl = "no-store";
        await http.Response.WriteAsync(body, Encoding.UTF8);
    }

    /// <summary>
    /// One audit line and one measurement per exchange. Identifiers only: no
    /// payload, no credential, no member data. The payload's SHA-256 lets an
    /// investigator match the line to the submitter's own copy.
    /// </summary>
    private void Record(CoreEnvelopeRequest? e, CoreEnvelopeResponse? r, CoreSubmitter? submitter, int status, string outcome, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        var payloadType = e?.PayloadType is { } t && _handlers.ContainsKey(t) ? t : "unknown";
        var errorCode = r?.ErrorCode ?? "none";

        ChoMetrics.CoreRealTimeDuration.Record(elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("cho.core.payload_type", payloadType),
            new KeyValuePair<string, object?>("cho.core.error_code", errorCode),
            new KeyValuePair<string, object?>("cho.core.outcome", outcome));

        _logger.LogInformation(
            "CORE real-time exchange. PayloadType={PayloadType} PayloadID={PayloadId} SenderID={SenderId} ReceiverID={ReceiverId} " +
            "Tenant={TenantId} Auth={AuthMethod} Format={Format} HttpStatus={HttpStatus} ErrorCode={ErrorCode} Outcome={Outcome} " +
            "ResponsePayloadType={ResponsePayloadType} PayloadSha256={PayloadSha256} ElapsedMs={ElapsedMs}",
            payloadType,
            LogSanitizer.Sanitize(e?.PayloadId),
            LogSanitizer.Sanitize(e?.SenderId),
            LogSanitizer.Sanitize(e?.ReceiverId),
            submitter?.TenantId,
            submitter?.Method.ToString() ?? CoreAuthenticationMethod.None.ToString(),
            e?.Format.ToString(),
            status,
            errorCode,
            outcome,
            r?.Payload is null ? null : r.PayloadType,
            e?.Payload is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(e.Payload))),
            (long)elapsed.TotalMilliseconds);
    }
}
