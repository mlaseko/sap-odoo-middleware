using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

public class BrandClassifierTests
{
    [Theory]
    [InlineData("vika", "vika", BrandClassifier.MatchKind.SameSupplier)]
    [InlineData("VIKA", "vika", BrandClassifier.MatchKind.SameSupplier)]      // case-insensitive
    [InlineData("Borsehung", "Borsehung", BrandClassifier.MatchKind.SameSupplier)]
    [InlineData(" vika ", "vika", BrandClassifier.MatchKind.SameSupplier)]    // trimmed
    [InlineData("vika", "DPA", BrandClassifier.MatchKind.DifferentSupplier)]
    [InlineData("Borsehung", "DPA", BrandClassifier.MatchKind.DifferentSupplier)]
    [InlineData("VAG", "Borsehung", BrandClassifier.MatchKind.VehicleGroupBrand)]
    [InlineData("BMW", "vika", BrandClassifier.MatchKind.VehicleGroupBrand)]
    [InlineData("MB", "Mahle", BrandClassifier.MatchKind.VehicleGroupBrand)]
    [InlineData("LR", "DPA", BrandClassifier.MatchKind.VehicleGroupBrand)]
    [InlineData("OE", "Borsehung", BrandClassifier.MatchKind.VehicleGroupBrand)]
    [InlineData("", "vika", BrandClassifier.MatchKind.NoBrandOnInvoice)]
    [InlineData(null, "vika", BrandClassifier.MatchKind.NoBrandOnInvoice)]
    public void Classify_ReturnsExpected(string? invoiceBrand, string donorSupplier, BrandClassifier.MatchKind expected)
        => Assert.Equal(expected, BrandClassifier.Classify(invoiceBrand, donorSupplier));
}
