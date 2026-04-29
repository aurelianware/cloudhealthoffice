using BenefitPlanService.Models;
using BenefitPlanService.Services;

namespace CloudHealthOffice.BenefitPlanService.Tests;

public class PlanDocumentValidationTests
{
    // Base64 SHA-256 of the empty string — 32 decoded bytes.
    private const string ValidSha256Base64 = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=";

    // Base64 SHA-1 of the empty string — 20 decoded bytes. Valid Base64
    // but wrong length.
    private const string Sha1Base64 = "2jmj7l5rSw0yVb/vlWAYkK/YBwk=";

    [Fact]
    public void ValidateHash_accepts_null()
    {
        PlanDocumentValidation.ValidateHash(null, "contentHashSha256");
    }

    [Fact]
    public void ValidateHash_accepts_empty_string()
    {
        PlanDocumentValidation.ValidateHash(string.Empty, "contentHashSha256");
    }

    [Fact]
    public void ValidateHash_accepts_valid_base64_sha256()
    {
        PlanDocumentValidation.ValidateHash(ValidSha256Base64, "contentHashSha256");
    }

    [Fact]
    public void ValidateHash_rejects_hex_string()
    {
        // A plain hex digest — not valid Base64 (or, in some cases,
        // parses as Base64 but to the wrong length). Either way it must
        // be rejected.
        var hex = "a1b2c3d4e5f607182930414253647586a7b8c9d0e1f20314253647586978a9b0";
        var ex = Assert.Throws<ArgumentException>(
            () => PlanDocumentValidation.ValidateHash(hex, "contentHashSha256"));
        Assert.Equal("contentHashSha256", ex.ParamName);
    }

    [Fact]
    public void ValidateHash_rejects_base64_with_wrong_length()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PlanDocumentValidation.ValidateHash(Sha1Base64, "contentHashSha256"));
        Assert.Equal("contentHashSha256", ex.ParamName);
        Assert.Contains("32 bytes", ex.Message);
    }

    [Fact]
    public void ValidateHash_rejects_garbage()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PlanDocumentValidation.ValidateHash("not base64!!!", "contentHashSha256"));
        Assert.Equal("contentHashSha256", ex.ParamName);
    }

    [Fact]
    public void ValidateDocuments_labels_index_in_param_name()
    {
        var docs = new List<PlanDocumentReference>
        {
            new() { DocType = PlanDocumentType.SBC, Location = "https://example.com/a.pdf", ContentHashSha256 = ValidSha256Base64 },
            new() { DocType = PlanDocumentType.EOC, Location = "https://example.com/b.pdf", ContentHashSha256 = "not-base64!" },
        };

        var ex = Assert.Throws<ArgumentException>(
            () => PlanDocumentValidation.ValidateDocuments(docs));
        Assert.Equal("documents[1].contentHashSha256", ex.ParamName);
    }

    [Fact]
    public void ValidateDocuments_accepts_null_collection()
    {
        PlanDocumentValidation.ValidateDocuments(null);
    }

    // ── ValidateLocation (capability BP 5.9) ────────────────────────────

    [Fact]
    public void ValidateLocation_accepts_https_url()
    {
        PlanDocumentValidation.ValidateLocation("https://example.com/sbc.pdf", "location");
    }

    [Fact]
    public void ValidateLocation_accepts_internal_documentreference_form()
    {
        PlanDocumentValidation.ValidateLocation("documentreference/abc-123", "location");
    }

    [Fact]
    public void ValidateLocation_rejects_null_or_empty()
    {
        Assert.Throws<ArgumentException>(
            () => PlanDocumentValidation.ValidateLocation(null, "location"));
        Assert.Throws<ArgumentException>(
            () => PlanDocumentValidation.ValidateLocation(string.Empty, "location"));
    }

    [Fact]
    public void ValidateLocation_rejects_plain_http()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PlanDocumentValidation.ValidateLocation("http://example.com/sbc.pdf", "location"));
        Assert.Equal("location", ex.ParamName);
        Assert.Contains("HTTPS", ex.Message);
    }

    [Fact]
    public void ValidateLocation_rejects_relative_url()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PlanDocumentValidation.ValidateLocation("/relative/sbc.pdf", "location"));
        Assert.Equal("location", ex.ParamName);
    }

    [Fact]
    public void ValidateLocation_rejects_documentreference_prefix_without_id()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PlanDocumentValidation.ValidateLocation("documentreference/", "location"));
        Assert.Equal("location", ex.ParamName);
    }

    [Fact]
    public void ValidateDocuments_validates_location_field()
    {
        var docs = new List<PlanDocumentReference>
        {
            new() { DocType = PlanDocumentType.SBC, Location = "https://example.com/a.pdf" },
            new() { DocType = PlanDocumentType.EOC, Location = "ftp://example.com/b.pdf" },
        };

        var ex = Assert.Throws<ArgumentException>(
            () => PlanDocumentValidation.ValidateDocuments(docs));
        Assert.Equal("documents[1].location", ex.ParamName);
    }
}
