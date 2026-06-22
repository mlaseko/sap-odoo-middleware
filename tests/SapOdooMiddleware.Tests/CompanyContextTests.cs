using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Tenant resolution + isolation unit tests. The cross-tenant 404 case
/// (LubesUserCannotReadAutohubDocuments) is a DB-backed integration check run on the Windows box —
/// it follows here for free because Lubes resolves to the MolasLUBES connection string and Autohub
/// to parts_catalog, so an Autohub document id is simply not present in the Lubes database.
/// </summary>
public class CompanyContextTests
{
    private const string LubesConn = "Host=lubes-host;Database=MolasLUBES";
    private const string PartsConn = "Host=parts-host;Database=parts_catalog";

    private static CompanyContext Build()
    {
        var companies = new CompaniesOptions();
        companies.Companies["Lubes"] = new CompanyConfig { DisplayName = "Molas Lubes", IsDefault = true };
        companies.Companies["Autohub"] = new CompanyConfig
        {
            DisplayName    = "Molas Autohub",
            Neon           = new NeonSettings { ConnectionString = PartsConn },
            Classifier     = new ClassifierSettings { BaseUrl = "http://dgx:8077" },
            VisionEndpoint = "/extract_parts_invoice",
            VisionModel    = "qwen2.5vl:32b-invoice",
        };

        return new CompanyContext(
            Options.Create(new NeonSettings { ConnectionString = LubesConn }),
            Options.Create(new DocumentIngestionSettings()),
            Options.Create(new ClassifierSettings()),
            Options.Create(new SapB1Settings()),
            Options.Create(new OdooSettings()),
            Options.Create(companies));
    }

    [Theory]
    [InlineData("/documents", "Lubes")]
    [InlineData("/", "Lubes")]
    [InlineData("/api/documents/123/status", "Lubes")]
    [InlineData("/documents/upload", "Lubes")]
    [InlineData("/autohub/documents", "Autohub")]
    [InlineData("/autohub/documents/upload", "Autohub")]
    [InlineData("/api/autohub/documents", "Autohub")]
    [InlineData("/autohubX", "Lubes")]   // segment-aware: not a real /autohub path
    public void ResolveCompanyKey_MapsUrlPrefixToTenant(string path, string expected)
    {
        Assert.Equal(expected, CompanyContext.ResolveCompanyKey(new PathString(path)));
    }

    [Fact]
    public void MissingTenantPrefixDefaultsToLubes()
    {
        var ctx = Build();
        Assert.Equal("Lubes", ctx.CurrentCompanyKey);
    }

    [Fact]
    public void LubesRequestUsesLubesConnectionString()
    {
        var ctx = Build();
        ctx.SetCompany("Lubes");

        Assert.Equal("Lubes", ctx.CurrentCompanyKey);
        Assert.Equal(LubesConn, ctx.Current.Neon.ConnectionString);
        Assert.Equal("/extract_invoice", ctx.Current.VisionEndpoint);
    }

    [Fact]
    public void AutohubRequestUsesPartsCatalogConnectionString()
    {
        var ctx = Build();
        ctx.SetCompany("Autohub");

        Assert.Equal("Autohub", ctx.CurrentCompanyKey);
        Assert.Equal(PartsConn, ctx.Current.Neon.ConnectionString);
        Assert.Equal("/extract_parts_invoice", ctx.Current.VisionEndpoint);
    }

    [Fact]
    public void UnknownTenantFallsBackToLubes()
    {
        var ctx = Build();
        ctx.SetCompany("Bogus");

        Assert.Equal("Lubes", ctx.CurrentCompanyKey);
        Assert.Equal(LubesConn, ctx.Current.Neon.ConnectionString);
    }

    [Fact]
    public void SetCompany_SwitchesResolvedConfig()
    {
        var ctx = Build();

        ctx.SetCompany("Autohub");
        Assert.Equal(PartsConn, ctx.Current.Neon.ConnectionString);

        ctx.SetCompany("Lubes");
        Assert.Equal(LubesConn, ctx.Current.Neon.ConnectionString);
    }
}
