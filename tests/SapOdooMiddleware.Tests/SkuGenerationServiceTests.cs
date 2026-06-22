using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

public class SkuGenerationServiceTests
{
    private sealed class FakeCounterRepo : ISkuCounterRepository
    {
        private readonly long _value;
        public string? LastPrefix { get; private set; }
        public FakeCounterRepo(long value) => _value = value;
        public Task<long> IncrementAsync(string prefix, CancellationToken ct)
        {
            LastPrefix = prefix;
            return Task.FromResult(_value);
        }
    }

    [Fact]
    public async Task Generate_FormatsPrefixPlusValue()
    {
        var svc = new SkuGenerationService(new FakeCounterRepo(100601));
        var code = await svc.GenerateAsync("LR", CancellationToken.None);
        Assert.Equal("LR100601", code);
    }

    [Fact]
    public async Task Generate_NormalisesPrefixToUpperCase()
    {
        var repo = new FakeCounterRepo(10001);
        var svc = new SkuGenerationService(repo);
        var code = await svc.GenerateAsync("  vag ", CancellationToken.None);
        Assert.Equal("VAG10001", code);
        Assert.Equal("VAG", repo.LastPrefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Generate_Throws_OnEmptyPrefix(string prefix)
    {
        var svc = new SkuGenerationService(new FakeCounterRepo(1));
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GenerateAsync(prefix, CancellationToken.None));
    }

    [Theory]
    [InlineData("MIN")]
    [InlineData("min")]
    [InlineData("MINI")]
    [InlineData(" mini ")]
    public async Task Generate_CanonicalisesMiniToFourCharPrefix(string input)
    {
        var repo = new FakeCounterRepo(10001);
        var svc = new SkuGenerationService(repo);
        var code = await svc.GenerateAsync(input, CancellationToken.None);
        Assert.Equal("MINI10001", code);
        Assert.Equal("MINI", repo.LastPrefix);   // counter keyed on the corrected 4-char prefix
    }
}
