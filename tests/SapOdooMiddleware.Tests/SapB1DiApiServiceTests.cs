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
}
