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
}
