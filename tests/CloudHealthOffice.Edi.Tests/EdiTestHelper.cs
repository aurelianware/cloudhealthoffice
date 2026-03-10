using Xunit;

namespace CloudHealthOffice.Edi.Tests;

/// <summary>
/// Shared helpers for parsing and asserting X12 EDI segments in tests.
/// </summary>
internal static class EdiTestHelper
{
    /// <summary>Split an EDI string into segments (each segment is an element array).</summary>
    internal static List<string[]> ParseSegments(string edi) =>
        edi.Split('~')
           .Select(s => s.Trim())
           .Where(s => s.Length > 0)
           .Select(s => s.Split('*'))
           .ToList();

    internal static string[] Segment(List<string[]> segs, string id) =>
        segs.First(s => s[0] == id);

    internal static bool HasSegment(List<string[]> segs, string id) =>
        segs.Any(s => s[0] == id);

    internal static List<string[]> AllSegments(List<string[]> segs, string id) =>
        segs.Where(s => s[0] == id).ToList();

    /// <summary>
    /// Assert SE01 = count of all segments from ST to SE inclusive.
    /// ISA / GS / GE / IEA are not counted.
    /// </summary>
    internal static void AssertSeCountCorrect(List<string[]> segs)
    {
        int stIdx = segs.FindIndex(s => s[0] == "ST");
        int seIdx = segs.FindIndex(s => s[0] == "SE");
        Assert.True(stIdx >= 0, "ST segment not found");
        Assert.True(seIdx  >= 0, "SE segment not found");

        int actualCount = seIdx - stIdx + 1; // ST..SE inclusive
        int declared    = int.Parse(segs[seIdx][1]);

        Assert.Equal(actualCount, declared);
    }
}
