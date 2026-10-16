using BlazorStudio.Deploy;

namespace BlazorStudio.Core.Tests;

public class MemoryTests
{
    private static MemoryTypeStat T(string type, long count, long bytes) => new() { Type = type, Count = count, Bytes = bytes };

    [Fact]
    public void Compare_sorts_by_growth_and_drops_unchanged_types()
    {
        var before = new[] { T("System.String", 100, 5000), T("MyApp.Order", 10, 800), T("System.Byte[]", 5, 10_000) };
        var after = new[] { T("System.String", 100, 5000), T("MyApp.Order", 1000, 80_000), T("MyApp.Cache", 1, 64) };

        var deltas = MemoryTypeDelta.Compare(before, after);

        Assert.Equal(new[] { "MyApp.Order", "MyApp.Cache", "System.Byte[]" }, deltas.Select(d => d.Type));
        Assert.Equal(990, deltas[0].CountDelta);
        Assert.Equal(79_200, deltas[0].BytesDelta);
        Assert.Equal(0, deltas[1].CountBefore);
        Assert.Equal(-10_000, deltas[2].BytesDelta);
        Assert.Equal(0, deltas[2].CountAfter);
    }

    [Theory]
    [InlineData("System.String", "String")]
    [InlineData("System.Collections.Generic.List<MyApp.Models.Order>", "List<Order>")]
    [InlineData("System.Collections.Generic.Dictionary<System.String, MyApp.Outer+Inner>", "Dictionary<String, Inner>")]
    [InlineData("System.Byte[]", "Byte[]")]
    [InlineData("Free", "Free")]
    public void ShortType_strips_namespaces(string input, string expected) =>
        Assert.Equal(expected, MemoryTypeDelta.ShortType(input));
}
