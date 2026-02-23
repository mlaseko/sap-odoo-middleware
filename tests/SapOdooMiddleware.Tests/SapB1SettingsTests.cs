using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Tests;

public class SapB1SettingsTests
{
    [Fact]
    public void SLDServer_DefaultValue_IsEmpty()
    {
        var settings = new SapB1Settings();

        Assert.Equal(string.Empty, settings.SLDServer);
    }

    [Fact]
    public void SLDServer_CanBeAssigned()
    {
        var settings = new SapB1Settings
        {
            SLDServer = "WIN-GJGQ73V0C3K:40000"
        };

        Assert.Equal("WIN-GJGQ73V0C3K:40000", settings.SLDServer);
    }

    [Fact]
    public void LicenseServer_DefaultValue_IsEmpty()
    {
        var settings = new SapB1Settings();

        Assert.Equal(string.Empty, settings.LicenseServer);
    }

    [Fact]
    public void LicenseServer_CanBeAssigned()
    {
        var settings = new SapB1Settings
        {
            LicenseServer = "license-host:30000"
        };

        Assert.Equal("license-host:30000", settings.LicenseServer);
    }

    [Fact]
    public void DefaultWarehouseCode_DefaultValue_IsMainWHSE()
    {
        var settings = new SapB1Settings();

        Assert.Equal("MainWHSE", settings.DefaultWarehouseCode);
    }

    [Fact]
    public void DefaultWarehouseCode_CanBeOverridden()
    {
        var settings = new SapB1Settings { DefaultWarehouseCode = "WAREHOUSE2" };

        Assert.Equal("WAREHOUSE2", settings.DefaultWarehouseCode);
    }
}
