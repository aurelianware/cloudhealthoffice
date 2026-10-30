using System.Text;
using Microsoft.AspNetCore.Http;

namespace CloudHealthOffice.Infrastructure.Edi.Core;

/// <summary>
/// HTTP MIME multipart (<c>multipart/form-data</c>) form of the CORE
/// real-time envelope: one form field per envelope field, the X12 in the
/// <c>Payload</c> field (as a value or a file part), and the submitter's
/// credentials in <c>UserName</c> / <c>Password</c> when it does not use a
/// client certificate or a bearer token.
/// </summary>
public static class CoreMimeCodec
{
    public const string ContentType = "multipart/form-data";

    public static async Task<CoreEnvelopeRequest> ParseAsync(IFormCollection form, CancellationToken ct)
    {
        string? payload = Value(form, CoreFields.Payload, trim: false);
        if (payload is null)
        {
            var file = form.Files.GetFile(CoreFields.Payload);
            if (file is not null)
            {
                using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
                payload = await reader.ReadToEndAsync(ct);
            }
        }

        return new CoreEnvelopeRequest
        {
            Format = CoreEnvelopeFormat.Mime,
            PayloadType = Value(form, CoreFields.PayloadType),
            ProcessingMode = Value(form, CoreFields.ProcessingMode),
            PayloadId = Value(form, CoreFields.PayloadId),
            PayloadLength = Value(form, CoreFields.PayloadLength),
            TimeStamp = Value(form, CoreFields.TimeStamp),
            SenderId = Value(form, CoreFields.SenderId),
            ReceiverId = Value(form, CoreFields.ReceiverId),
            CoreRuleVersion = Value(form, CoreFields.CoreRuleVersion),
            Checksum = Value(form, CoreFields.Checksum),
            UserName = Value(form, CoreFields.UserName),
            Password = Value(form, CoreFields.Password, trim: false),
            Payload = payload,
        };
    }

    /// <summary>The response body and its Content-Type (which carries the boundary).</summary>
    public static (string Body, string ContentType) Write(CoreEnvelopeResponse response)
    {
        var boundary = "CORE-" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        Part(sb, boundary, CoreFields.PayloadType, response.PayloadType);
        Part(sb, boundary, CoreFields.ProcessingMode, response.ProcessingMode);
        Part(sb, boundary, CoreFields.PayloadId, response.PayloadId);
        Part(sb, boundary, CoreFields.TimeStamp, response.TimeStamp);
        Part(sb, boundary, CoreFields.SenderId, response.SenderId);
        Part(sb, boundary, CoreFields.ReceiverId, response.ReceiverId);
        Part(sb, boundary, CoreFields.CoreRuleVersion, response.CoreRuleVersion);
        if (response.Payload is not null)
        {
            Part(sb, boundary, CoreFields.PayloadLength, Encoding.UTF8.GetByteCount(response.Payload).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Part(sb, boundary, CoreFields.Payload, response.Payload);
        }
        Part(sb, boundary, CoreFields.ErrorCode, response.ErrorCode);
        Part(sb, boundary, CoreFields.ErrorMessage, response.ErrorMessage ?? (response.ErrorCode == CoreErrorCodes.Success ? "None" : null));
        sb.Append("--").Append(boundary).Append("--\r\n");
        return (sb.ToString(), $"{ContentType}; boundary=\"{boundary}\"");
    }

    private static string? Value(IFormCollection form, string name, bool trim = true)
    {
        if (!form.TryGetValue(name, out var values) || values.Count == 0) return null;
        var value = values[0];
        if (value is null) return null;
        if (trim) value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    private static void Part(StringBuilder sb, string boundary, string name, string? value)
    {
        if (value is null) return;
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Disposition: form-data; name=\"").Append(name).Append("\"\r\n\r\n");
        sb.Append(value).Append("\r\n");
    }
}
