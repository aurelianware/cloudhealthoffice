using CloudHealthOffice.Infrastructure.Logging;
using FluentAssertions;
using Xunit;

namespace CloudHealthOffice.Infrastructure.Tests.Logging;

public class LogSanitizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitize_NullOrEmpty_ReturnsEmpty(string? value)
    {
        LogSanitizer.Sanitize(value).Should().BeEmpty();
        LogSanitizer.Sanitize(value, 10).Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_PlainValue_IsUnchanged()
    {
        const string value = "tenant-01 / CLM.123 (é ü 日本)";
        LogSanitizer.Sanitize(value).Should().Be(value);
    }

    [Theory]
    [InlineData("a\rb", "a_b")]
    [InlineData("a\nb", "a_b")]
    [InlineData("a\r\nb", "a__b")]
    [InlineData("a\tb", "a_b")]
    [InlineData("a\0b", "a_b")]
    [InlineData("a\vb\fc", "a_b_c")]
    [InlineData("a\u001bb", "a_b")]   // ESC: terminal escape sequences
    [InlineData("a\u007fb", "a_b")]   // DEL
    [InlineData("a\u0085b", "a_b")]   // NEL
    [InlineData("a\u2028b", "a_b")]   // line separator
    [InlineData("a\u2029b", "a_b")]   // paragraph separator
    public void Sanitize_ReplacesEveryLineBreakingOrControlCharacter(string value, string expected)
    {
        LogSanitizer.Sanitize(value).Should().Be(expected);
    }

    [Fact]
    public void Sanitize_ForgedEntry_StaysOnOneLine()
    {
        var forged = "abc\r\n2026-01-01 INFO admin logged in\u2028x";

        var result = LogSanitizer.Sanitize(forged);

        result.Should().NotContainAny("\r", "\n", "\u2028");
        result.Should().HaveLength(forged.Length);
    }

    [Fact]
    public void Sanitize_EveryControlCharacter_IsReplaced()
    {
        for (var c = char.MinValue; c < char.MaxValue; c++)
        {
            if (!char.IsControl(c)) continue;
            LogSanitizer.Sanitize($"x{c}y").Should().Be("x_y", $"U+{(int)c:X4} is a control character");
        }
    }

    [Fact]
    public void Sanitize_WithMaxLength_Truncates()
    {
        LogSanitizer.Sanitize("abcdef", 4).Should().Be("abcd");
        LogSanitizer.Sanitize("ab\ncdef", 4).Should().Be("ab_c");
        LogSanitizer.Sanitize("abc", 4).Should().Be("abc");
        LogSanitizer.Sanitize("abcd", 4).Should().Be("abcd");
    }

    [Fact]
    public void Sanitize_WithoutMaxLength_DoesNotTruncate()
    {
        var value = new string('a', 10_000);
        LogSanitizer.Sanitize(value).Should().HaveLength(10_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Sanitize_NonPositiveMaxLength_Throws(int maxLength)
    {
        var act = () => LogSanitizer.Sanitize("abc", maxLength);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("1", "***")]
    [InlineData("1234", "***")]
    [InlineData("12345", "***2345")]
    [InlineData("M00012345", "***2345")]
    [InlineData("SUB-000-998877", "***8877")]
    public void MaskId_KeepsOnlyTheLastFourCharacters(string? id, string expected)
    {
        LogSanitizer.MaskId(id).Should().Be(expected);
    }

    [Fact]
    public void MaskId_SanitizesTheVisibleTail()
    {
        LogSanitizer.MaskId("M0001\r\n23").Should().Be("***__23");
        LogSanitizer.MaskId("M0001\u202823").Should().Be("***1_23");
    }

    [Fact]
    public void MaskId_NeverContainsTheIdentifier()
    {
        const string id = "MBR987654321";
        LogSanitizer.MaskId(id).Should().NotContain("987654").And.EndWith("4321");
    }
}
