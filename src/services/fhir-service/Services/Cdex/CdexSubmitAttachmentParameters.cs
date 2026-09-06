using Hl7.Fhir.Model;

namespace FhirService.Services.Cdex;

/// <summary>One attachment as it arrived on the wire, before any policy check.</summary>
public sealed record CdexOfferedAttachment
{
    /// <summary>Decoded bytes, when the submitter sent them inline.</summary>
    public byte[]? Content { get; init; }

    public string? ContentType { get; init; }

    /// <summary>Caller-supplied title. Metadata only — never a storage path.</summary>
    public string? Title { get; init; }

    /// <summary>Where the submitter pointed instead of sending content. Never dereferenced.</summary>
    public string? Url { get; init; }

    /// <summary>The document type the submitter says this satisfies.</summary>
    public string? DocumentTypeCode { get; init; }

    public string? DocumentTypeSystem { get; init; }
}

/// <summary>
/// Reads the CDex <c>$submit-attachment</c> <c>Parameters</c> resource.
///
/// Kept apart from the submission service because "what the wire format says"
/// and "what CHO will accept" are different concerns: this type is tolerant
/// where the specification allows variation, and the service is strict about
/// what it then does with the result.
///
/// TOLERANCE, deliberately bounded. CDex defines <c>AttachTo</c> as an
/// Identifier; a plain string is also read, because senders in the field emit
/// both and refusing the string form would reject a submission whose meaning is
/// unambiguous. Content, by contrast, is read ONLY from inline
/// <c>Attachment.data</c> (or the <c>content.attachment</c> of a supplied
/// DocumentReference): a URL is captured so it can be refused explicitly, and is
/// never fetched.
///
/// NOTHING HERE IS AUTHORITY. The tenant is not read from this payload at all —
/// not from a parameter, not from an identifier system, not from an
/// Organization reference. It comes from the authenticated context and only from
/// there.
/// </summary>
public static class CdexSubmitAttachmentParameters
{
    public const string TrackingIdParameter = "TrackingId";
    public const string AttachToParameter = "AttachTo";
    public const string OrganizationParameter = "Organization";
    public const string ProviderParameter = "Provider";
    public const string AttachmentParameter = "Attachment";
    public const string ContentPart = "Content";
    public const string CodePart = "Code";

    /// <summary>The payer's attachment control number for the request being answered.</summary>
    public static string? TrackingId(Parameters parameters)
    {
        var value = Find(parameters, TrackingIdParameter)?.Value;

        return value switch
        {
            FhirString s => Trimmed(s.Value),
            Identifier id => Trimmed(id.Value),
            _ => null,
        };
    }

    /// <summary>The claim or prior authorization the documentation belongs to.</summary>
    public static string? AttachTo(Parameters parameters)
    {
        var value = Find(parameters, AttachToParameter)?.Value;

        return value switch
        {
            Identifier id => Trimmed(id.Value),
            FhirString s => Trimmed(s.Value),
            _ => null,
        };
    }

    /// <summary>
    /// The submitting provider's NPI, from <c>Provider.identifier</c>. An NPI is
    /// a corroborating key here, not proof of identity — see the submission
    /// service's caller-binding note.
    /// </summary>
    public static string? ProviderNpi(Parameters parameters)
    {
        var provider = Find(parameters, ProviderParameter);

        if (provider?.Value is ResourceReference reference)
        {
            var identifier = reference.Identifier;
            if (!string.IsNullOrWhiteSpace(identifier?.Value))
                return Trimmed(identifier!.Value);

            // "Organization/1234567890" — the id segment, when the sender gave
            // a literal reference rather than an identifier.
            if (!string.IsNullOrWhiteSpace(reference.Reference))
            {
                var slash = reference.Reference.LastIndexOf('/');
                if (slash >= 0 && slash < reference.Reference.Length - 1)
                    return Trimmed(reference.Reference[(slash + 1)..]);
            }
        }

        return provider?.Value switch
        {
            Identifier id => Trimmed(id.Value),
            FhirString s => Trimmed(s.Value),
            _ => null,
        };
    }

    /// <summary>
    /// Every artifact offered. Two shapes are read: a repeating
    /// <c>Attachment</c> parameter with a <c>Content</c> part carrying an
    /// <c>Attachment</c>, and a supplied <c>DocumentReference</c> resource whose
    /// <c>content.attachment</c> carries it. Both appear in the field; both mean
    /// the same thing.
    /// </summary>
    public static IReadOnlyList<CdexOfferedAttachment> Attachments(Parameters parameters)
    {
        var results = new List<CdexOfferedAttachment>();

        foreach (var parameter in parameters.Parameter ?? [])
        {
            if (!string.Equals(parameter.Name, AttachmentParameter, StringComparison.Ordinal))
                continue;

            var (code, system) = ReadCode(parameter);

            // Shape 1: value directly on the Attachment parameter.
            if (parameter.Value is Attachment direct)
            {
                results.Add(ToOffered(direct, code, system));
                continue;
            }

            // Shape 2: a Content part.
            var contentPart = parameter.Part?
                .FirstOrDefault(p => string.Equals(p.Name, ContentPart, StringComparison.Ordinal));

            if (contentPart?.Value is Attachment fromPart)
            {
                results.Add(ToOffered(fromPart, code, system));
                continue;
            }

            // Shape 3: a DocumentReference resource, on the parameter or the part.
            var document = parameter.Resource as DocumentReference
                           ?? contentPart?.Resource as DocumentReference;

            if (document is not null)
            {
                foreach (var content in document.Content ?? [])
                {
                    if (content.Attachment is null) continue;

                    var documentCode = document.Type?.Coding?.FirstOrDefault();
                    results.Add(ToOffered(
                        content.Attachment,
                        code ?? documentCode?.Code,
                        system ?? documentCode?.System));
                }
            }
        }

        return results;
    }

    private static (string? Code, string? System) ReadCode(Parameters.ParameterComponent parameter)
    {
        var codePart = parameter.Part?
            .FirstOrDefault(p => string.Equals(p.Name, CodePart, StringComparison.Ordinal));

        var coding = (codePart?.Value as CodeableConcept)?.Coding?.FirstOrDefault()
                     ?? codePart?.Value as Coding;

        return (coding?.Code, coding?.System);
    }

    private static CdexOfferedAttachment ToOffered(Attachment attachment, string? code, string? system)
        => new()
        {
            Content = attachment.Data,
            ContentType = Trimmed(attachment.ContentType),
            Title = attachment.Title,
            Url = attachment.Url,
            DocumentTypeCode = code,
            DocumentTypeSystem = system,
        };

    private static Parameters.ParameterComponent? Find(Parameters parameters, string name)
        => parameters.Parameter?
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
