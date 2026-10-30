using System.Text;
using System.Xml;

namespace CloudHealthOffice.Infrastructure.Edi.Core;

/// <summary>
/// SOAP 1.2 form of the CORE real-time envelope: a
/// <c>COREEnvelopeRealTimeRequest</c> body, with the submitter's credentials
/// in a WS-Security 1.1 <c>UsernameToken</c> header when it does not use a
/// client certificate or a bearer token.
///
/// <para>Element namespaces are matched by local name: some submitters
/// qualify the child elements, most (as in the rule's examples) do not.
/// DTDs and external entities are refused.</para>
/// </summary>
public static class CoreSoapCodec
{
    public const string ContentType = "application/soap+xml";

    public const string RequestElement = "COREEnvelopeRealTimeRequest";
    public const string ResponseElement = "COREEnvelopeRealTimeResponse";

    /// <summary>Reads a SOAP request. Throws <see cref="CoreEnvelopeFormatException"/> when it is not one.</summary>
    public static CoreEnvelopeRequest Parse(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        var doc = new XmlDocument { XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new CoreEnvelopeFormatException("The request is not well-formed XML.", ex);
        }

        var envelope = doc.DocumentElement;
        if (envelope is null || envelope.LocalName != "Envelope" || envelope.NamespaceURI != CoreRule.Soap12Namespace)
            throw new CoreEnvelopeFormatException("The request is not a SOAP 1.2 envelope.");

        var body = Child(envelope, "Body") ?? throw new CoreEnvelopeFormatException("The SOAP envelope has no Body.");
        var request = Child(body, RequestElement)
            ?? throw new CoreEnvelopeFormatException($"The SOAP Body has no {RequestElement}.");

        string? userName = null, password = null;
        var token = Child(Child(Child(envelope, "Header"), "Security"), "UsernameToken");
        if (token is not null)
        {
            userName = Child(token, "Username")?.InnerText;
            var passwordElement = Child(token, "Password");
            // Only a clear-text password can be checked against a stored hash;
            // PasswordDigest needs the clear password on the server.
            var type = passwordElement?.GetAttribute("Type");
            if (passwordElement is not null && (string.IsNullOrEmpty(type) || type.EndsWith("#PasswordText", StringComparison.Ordinal)))
                password = passwordElement.InnerText;
        }

        return new CoreEnvelopeRequest
        {
            Format = CoreEnvelopeFormat.Soap,
            PayloadType = Text(request, CoreFields.PayloadType),
            ProcessingMode = Text(request, CoreFields.ProcessingMode),
            PayloadId = Text(request, CoreFields.PayloadId),
            PayloadLength = Text(request, CoreFields.PayloadLength),
            TimeStamp = Text(request, CoreFields.TimeStamp),
            SenderId = Text(request, CoreFields.SenderId),
            ReceiverId = Text(request, CoreFields.ReceiverId),
            CoreRuleVersion = Text(request, CoreFields.CoreRuleVersion),
            Checksum = Text(request, CoreFields.Checksum),
            // The payload is kept exactly as sent (no trim): X12 is position-sensitive.
            Payload = Child(request, CoreFields.Payload)?.InnerText,
            UserName = userName,
            Password = password,
        };
    }

    public static string Write(CoreEnvelopeResponse response)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<soapenv:Envelope xmlns:soapenv=\"").Append(CoreRule.Soap12Namespace).Append("\">");
        sb.Append("<soapenv:Header/><soapenv:Body>");
        sb.Append("<cor:").Append(ResponseElement).Append(" xmlns:cor=\"").Append(CoreRule.XsdNamespace).Append("\">");
        Element(sb, CoreFields.PayloadType, response.PayloadType);
        Element(sb, CoreFields.ProcessingMode, response.ProcessingMode);
        Element(sb, CoreFields.PayloadId, response.PayloadId);
        Element(sb, CoreFields.TimeStamp, response.TimeStamp);
        Element(sb, CoreFields.SenderId, response.SenderId);
        Element(sb, CoreFields.ReceiverId, response.ReceiverId);
        Element(sb, CoreFields.CoreRuleVersion, response.CoreRuleVersion);
        if (response.Payload is not null)
            sb.Append('<').Append(CoreFields.Payload).Append('>').Append(CData(response.Payload)).Append("</").Append(CoreFields.Payload).Append('>');
        Element(sb, CoreFields.ErrorCode, response.ErrorCode);
        Element(sb, CoreFields.ErrorMessage, response.ErrorMessage ?? (response.ErrorCode == CoreErrorCodes.Success ? "None" : null));
        sb.Append("</cor:").Append(ResponseElement).Append('>');
        sb.Append("</soapenv:Body></soapenv:Envelope>");
        return sb.ToString();
    }

    /// <summary>A SOAP 1.2 Fault, for a request that is not a readable CORE envelope at all.</summary>
    public static string WriteFault(string reason, bool senderFault = true)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<soapenv:Envelope xmlns:soapenv=\"").Append(CoreRule.Soap12Namespace).Append("\"><soapenv:Body><soapenv:Fault>");
        sb.Append("<soapenv:Code><soapenv:Value>soapenv:").Append(senderFault ? "Sender" : "Receiver").Append("</soapenv:Value></soapenv:Code>");
        sb.Append("<soapenv:Reason><soapenv:Text xml:lang=\"en\">").Append(Escape(reason)).Append("</soapenv:Text></soapenv:Reason>");
        sb.Append("</soapenv:Fault></soapenv:Body></soapenv:Envelope>");
        return sb.ToString();
    }

    private static XmlElement? Child(XmlElement? parent, string localName)
    {
        if (parent is null) return null;
        foreach (XmlNode node in parent.ChildNodes)
            if (node is XmlElement e && e.LocalName == localName)
                return e;
        return null;
    }

    private static string? Text(XmlElement parent, string localName)
    {
        var value = Child(parent, localName)?.InnerText.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static void Element(StringBuilder sb, string name, string? value)
    {
        if (value is null) return;
        sb.Append('<').Append(name).Append('>').Append(Escape(value)).Append("</").Append(name).Append('>');
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // "]]>" cannot appear inside one CDATA section; split it across two.
    private static string CData(string value) =>
        "<![CDATA[" + value.Replace("]]>", "]]]]><![CDATA[>") + "]]>";
}

/// <summary>The request cannot be read as a CORE envelope (malformed XML, wrong body, unreadable multipart).</summary>
public sealed class CoreEnvelopeFormatException : Exception
{
    public CoreEnvelopeFormatException(string message) : base(message) { }
    public CoreEnvelopeFormatException(string message, Exception inner) : base(message, inner) { }
}
