using SapOdooMiddleware.Models.Sap;

namespace SapOdooMiddleware.Tests;

public class SapB1PingResponseTests
{
    [Fact]
    public void DefaultValues_AreExpected()
    {
        var response = new SapB1PingResponse();

        Assert.False(response.Connected);
        Assert.Equal(string.Empty, response.Server);
        Assert.Equal(string.Empty, response.CompanyDb);
        Assert.Equal(string.Empty, response.LicenseServer);
        Assert.Equal(string.Empty, response.SldServer);
        Assert.Null(response.CompanyName);
        Assert.Null(response.Version);
    }

    [Fact]
    public void CanSetAllProperties()
    {
        var response = new SapB1PingResponse
        {
            Connected = true,
            Server = "sql-host",
            CompanyDb = "SBODemoUS",
            LicenseServer = "license-host:30000",
            SldServer = "sld-host:40000",
            CompanyName = "Demo Company",
            Version = "10.0"
        };

        Assert.True(response.Connected);
        Assert.Equal("sql-host", response.Server);
        Assert.Equal("SBODemoUS", response.CompanyDb);
        Assert.Equal("license-host:30000", response.LicenseServer);
        Assert.Equal("sld-host:40000", response.SldServer);
        Assert.Equal("Demo Company", response.CompanyName);
        Assert.Equal("10.0", response.Version);
    }
}
