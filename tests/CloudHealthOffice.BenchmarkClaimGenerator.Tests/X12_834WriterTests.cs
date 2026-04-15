using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;
using CloudHealthOffice.BenchmarkClaimGenerator.Generators;
using CloudHealthOffice.BenchmarkClaimGenerator.Models;
using CloudHealthOffice.BenchmarkClaimGenerator.Output;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class X12_834WriterTests : IDisposable
{
    private readonly string _outputDir;
    private readonly List<SyntheticMember> _members;

    public X12_834WriterTests()
    {
        _outputDir = Path.Combine(Path.GetTempPath(), $"mcc-834-test-{Guid.NewGuid():N}");
        var profile = new MemberPoolProfile
        {
            SubscriberCount = 50,
            Seed = 42,
            TenantId = "test-tenant",
        };
        var plans = SyntheticBenefitPlanGenerator.Generate(42);
        var generator = new SyntheticMemberGenerator();
        _members = generator.Generate(profile, plans);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, true);
    }

    [Fact]
    public async Task WriteAsync_CreatesOutputFiles()
    {
        var writer = new X12_834Writer(_outputDir);
        var files = await writer.WriteAsync(_members);

        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public async Task WriteAsync_FilesHaveCorrectNaming()
    {
        var writer = new X12_834Writer(_outputDir);
        var files = await writer.WriteAsync(_members);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            Assert.Matches(@"MCC-834-\d{4}\.edi", fileName);
        }
    }

    [Fact]
    public async Task WriteAsync_FilePassesStructuralValidation()
    {
        var writer = new X12_834Writer(_outputDir);
        var files = await writer.WriteAsync(_members);

        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file);
            Assert.True(X12_834Writer.ValidateStructure(content),
                $"File {file} failed structural validation");
        }
    }

    [Fact]
    public async Task WriteAsync_FileStartsWithISA()
    {
        var writer = new X12_834Writer(_outputDir);
        var files = await writer.WriteAsync(_members);

        foreach (var file in files)
        {
            var content = await File.ReadAllTextAsync(file);
            Assert.StartsWith("ISA", content);
        }
    }

    [Fact]
    public async Task WriteAsync_FileContainsExpectedSegments()
    {
        var writer = new X12_834Writer(_outputDir);
        var files = await writer.WriteAsync(_members);

        var content = await File.ReadAllTextAsync(files[0]);

        // Must contain required envelope segments
        Assert.Contains("GS*HP", content);
        Assert.Contains("ST*834", content);
        Assert.Contains("BGN*00", content);
        Assert.Contains("N1*P5", content); // Sponsor
        Assert.Contains("N1*IN", content); // Payer
        Assert.Contains("INS*Y*18", content); // Subscriber
        Assert.Contains("NM1*IL", content); // Member name
        Assert.Contains("DMG*D8", content); // Demographics
        Assert.Contains("HD*021", content); // Health coverage
        Assert.Contains("DTP*348", content); // Benefit begin
        Assert.Contains("SE*", content); // Trailer
        Assert.Contains("GE*", content);
        Assert.Contains("IEA*", content);
    }

    [Fact]
    public async Task WriteAsync_UsesCorrectDelimiters()
    {
        var writer = new X12_834Writer(_outputDir);
        var files = await writer.WriteAsync(_members);

        var content = await File.ReadAllTextAsync(files[0]);

        // Segment terminator is ~
        Assert.Contains("~", content);
        // Element separator is *
        Assert.Contains("*", content);
    }

    [Fact]
    public void ValidateStructure_EmptyContent_ReturnsFalse()
    {
        Assert.False(X12_834Writer.ValidateStructure(""));
        Assert.False(X12_834Writer.ValidateStructure(null!));
    }

    [Fact]
    public void ValidateStructure_InvalidContent_ReturnsFalse()
    {
        Assert.False(X12_834Writer.ValidateStructure("NOT AN 834 FILE"));
    }
}
