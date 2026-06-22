using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

public class OemFilterServiceTests
{
    private readonly OemFilterService _svc = new();

    [Fact]
    public void Filter_SeparatesCleanOemsFromNoiseTokens()
    {
        var raw = new[] { "LR029078", "BJ329601AA", "Front Right", "3.0L", "Non Electrical", "Rear" };

        var result = _svc.Filter(raw, "GL0569", "GERMAX");

        Assert.Equal(new[] { "LR029078", "BJ329601AA" }, result.CleanOems);
        Assert.Contains("Front Right", result.NoiseFiltered);
        Assert.Contains("3.0L", result.NoiseFiltered);
        Assert.Contains("Non Electrical", result.NoiseFiltered);
        Assert.Contains("Rear", result.NoiseFiltered);
        Assert.Equal("regex_blacklist", result.Source);
    }

    [Fact]
    public void Filter_TokenWithoutDigit_IsTreatedAsNoise()
    {
        var result = _svc.Filter(new[] { "ABCDEF" }, null, null);
        Assert.Empty(result.CleanOems);
        Assert.Contains("ABCDEF", result.NoiseFiltered);
        Assert.Equal("all_filtered", result.Source);
    }

    [Fact]
    public void Filter_SkipsEmptyAndWhitespaceTokens()
    {
        var result = _svc.Filter(new[] { "", "   ", "LR029078" }, null, null);
        Assert.Equal(new[] { "LR029078" }, result.CleanOems);
        Assert.Empty(result.NoiseFiltered);
    }

    [Fact]
    public void Filter_TrimsTokensBeforeMatching()
    {
        var result = _svc.Filter(new[] { "  LR029078  " }, null, null);
        Assert.Equal(new[] { "LR029078" }, result.CleanOems);
    }

    [Fact]
    public void Filter_EngineSizeTokens_AreNoise()
    {
        var result = _svc.Filter(new[] { "5.0L", "3.6L", "V8" }, null, null);
        Assert.Empty(result.CleanOems);
        Assert.Equal(3, result.NoiseFiltered.Count);
    }

    [Theory]
    [InlineData("GERMAX", true)]
    [InlineData("germax parts", true)]
    [InlineData("GAPC", true)]
    [InlineData("vika", false)]
    [InlineData(null, false)]
    public void IsGermaxBrand_DetectsGermaxSuppliers(string? brand, bool expected)
        => Assert.Equal(expected, OemFilterService.IsGermaxBrand(brand));
}
