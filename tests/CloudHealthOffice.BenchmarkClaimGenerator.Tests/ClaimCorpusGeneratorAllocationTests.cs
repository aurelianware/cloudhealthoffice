using System.Reflection;
using CloudHealthOffice.BenchmarkClaimGenerator.ReferenceData;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class ClaimCorpusGeneratorAllocationTests
{
    [Fact]
    public void AllocateCounts_WithZeroTotal_ReturnsZeroEntries()
    {
        var allocations = InvokeAllocateCounts(0, [("a", 0.6), ("b", 0.4)]);

        Assert.Collection(allocations,
            item =>
            {
                Assert.Equal("a", item.Item);
                Assert.Equal(0, item.Count);
            },
            item =>
            {
                Assert.Equal("b", item.Item);
                Assert.Equal(0, item.Count);
            });
    }

    [Fact]
    public void AllocateCounts_NormalizesWeights_AndPreservesRequestedTotal()
    {
        var allocations = InvokeAllocateCounts(5, [("a", 3.0), ("b", 1.0), ("c", 0.0)]);

        Assert.Equal(5, allocations.Sum(item => item.Count));
        Assert.Collection(allocations,
            item =>
            {
                Assert.Equal("a", item.Item);
                Assert.Equal(4, item.Count);
            },
            item =>
            {
                Assert.Equal("b", item.Item);
                Assert.Equal(1, item.Count);
            },
            item =>
            {
                Assert.Equal("c", item.Item);
                Assert.Equal(0, item.Count);
            });
    }

    private static IReadOnlyList<(string Item, int Count)> InvokeAllocateCounts(
        int total,
        IReadOnlyList<(string Item, double Fraction)> weightedItems)
    {
        var method = typeof(ClaimCorpusGenerator)
            .GetMethod("AllocateCounts", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(string));

        return (IReadOnlyList<(string Item, int Count)>)method.Invoke(
            null,
            [total, weightedItems])!;
    }
}
