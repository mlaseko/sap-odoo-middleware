using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Tests;

public class OdooSettingsTests
{
    [Fact]
    public void ApiKey_DefaultValue_IsEmpty()
    {
        var settings = new OdooSettings();
        Assert.Equal(string.Empty, settings.ApiKey);
    }

    [Fact]
    public void UseBearerAuth_WhenApiKeyIsSet_ReturnsTrue()
    {
        var settings = new OdooSettings { ApiKey = "my-api-key" };
        Assert.True(settings.UseBearerAuth);
    }

    [Fact]
    public void UseBearerAuth_WhenApiKeyIsEmpty_ReturnsFalse()
    {
        var settings = new OdooSettings { ApiKey = string.Empty };
        Assert.False(settings.UseBearerAuth);
    }

    [Fact]
    public void UseBearerAuth_WhenApiKeyIsWhitespace_ReturnsFalse()
    {
        var settings = new OdooSettings { ApiKey = "   " };
        Assert.False(settings.UseBearerAuth);
    }

    [Fact]
    public void EffectiveApiKey_WhenApiKeyIsSet_ReturnsApiKey()
    {
        var settings = new OdooSettings { ApiKey = "api-key-value", Password = "password-value" };
        Assert.Equal("api-key-value", settings.EffectiveApiKey);
    }

    [Fact]
    public void EffectiveApiKey_WhenApiKeyIsEmpty_FallsBackToPassword()
    {
        var settings = new OdooSettings { ApiKey = string.Empty, Password = "password-value" };
        Assert.Equal("password-value", settings.EffectiveApiKey);
    }

    [Fact]
    public void EffectiveApiKey_WhenBothEmpty_ReturnsEmpty()
    {
        var settings = new OdooSettings { ApiKey = string.Empty, Password = string.Empty };
        Assert.Equal(string.Empty, settings.EffectiveApiKey);
    }

    [Fact]
    public void SectionName_IsOdoo()
    {
        Assert.Equal("Odoo", OdooSettings.SectionName);
    }
}
