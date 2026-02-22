using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class SapB1DiApiServiceTests
{
    [Fact]
    public void Constructor_LogsConfigurationSummary()
    {
        // Arrange
        var settings = new SapB1Settings
        {
            Server = "test-server",
            CompanyDb = "TestDB",
            UserName = "testuser",
            Password = "secret",
            DbServerType = "dst_MSSQL2019",
            LicenseServer = "license:30000",
            SLDServer = "sld:40000"
        };

        var options = Options.Create(settings);
        var loggerMock = new Mock<ILogger<SapB1DiApiService>>();

        // Act
        using var service = new SapB1DiApiService(options, loggerMock.Object);

        // Assert — verify the constructor logged an Information message
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("SAP DI API config loaded")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Theory]
    [InlineData("dst_MSSQL2016", new[] { 8, 10 })]
    [InlineData("MSSQL2016", new[] { 8, 10 })]
    [InlineData("mssql2016", new[] { 8, 10 })]
    [InlineData("dst_MSSQL2019", new[] { 10, 16 })]
    [InlineData("MSSQL2019", new[] { 10, 16 })]
    [InlineData("dst_MSSQL2017", new[] { 9, 15 })]
    [InlineData("dst_MSSQL2014", new[] { 7, 8 })]
    [InlineData("dst_MSSQL2012", new[] { 6, 7 })]
    [InlineData("dst_MSSQL2008", new[] { 5, 6 })]
    [InlineData("MSSQL", new[] { 1 })]
    [InlineData("MSSQL2005", new[] { 4 })]
    [InlineData("HANADB", new[] { 11, 9 })]
    [InlineData("dst_HANADB", new[] { 11, 9 })]
    public void MapDbServerTypeCandidates_ReturnsCandidatesForKnownTypes(string input, int[] expected)
    {
        var result = SapB1DiApiService.MapDbServerTypeCandidates(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("dst_MSSQL2016")]
    [InlineData("dst_MSSQL2019")]
    [InlineData("dst_MSSQL2017")]
    [InlineData("dst_MSSQL2014")]
    [InlineData("dst_MSSQL2012")]
    [InlineData("dst_MSSQL2008")]
    [InlineData("HANADB")]
    public void MapDbServerTypeCandidates_ReturnsMultipleCandidatesForAmbiguousTypes(string input)
    {
        var result = SapB1DiApiService.MapDbServerTypeCandidates(input);

        Assert.True(result.Length > 1, $"Expected multiple candidates for '{input}' but got {result.Length}.");
    }

    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData("")]
    [InlineData("dst_ORACLE")]
    public void MapDbServerTypeCandidates_ThrowsForUnrecognizedType(string input)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => SapB1DiApiService.MapDbServerTypeCandidates(input));

        Assert.Contains("Unrecognized DbServerType", ex.Message);
    }
}
