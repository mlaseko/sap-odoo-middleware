using SapOdooMiddleware.Services.Autohub;
using Xunit;

namespace SapOdooMiddleware.Tests;

/// <summary>Same cases as the DGX's test_donor_selection.py: the two must agree.</summary>
public class PartNameMatchTests
{
    [Theory]
    [InlineData("Diesel fuel filter", "Fuel Filter")]
    [InlineData("Starter; 12V; 1.7KW; CW; 13 teeth", "Starter")]
    [InlineData("Alternator; 12V; 180A; CW", "Alternator")]
    [InlineData("Alternator; 12V; 180A; CW", "Generator")]
    [InlineData("Lambda sensor; 4 wires", "Oxygen Sensor")]
    [InlineData("Turbo charger", "Charger, charging (supercharged/turbocharged)")]
    [InlineData("Starter motor", "Starter")]
    [InlineData("Water pump", "Coolant pump")]
    [InlineData("Brake disc", "Brake Disc")]
    [InlineData("Wishbone", "Control Arm/Trailing Arm, wheel suspension")]
    [InlineData("Brake pad set", "Brake Pad Set, disc brake")]
    public void Same_part_under_another_name_matches(string invoice, string part) =>
        Assert.Equal(PartNameMatch.Match, PartNameMatch.Verdict(invoice, part));

    [Theory]
    [InlineData("Brake pad set", "Warning Contact, brake pad wear", PartNameMatch.Different)]
    [InlineData("Brake pad set", "Brake Disc", PartNameMatch.Uncertain)]
    [InlineData("Ignition coil", "Spark Plug", PartNameMatch.Uncertain)]
    [InlineData("Mounting for track control arm; right", "Control Arm/Trailing Arm, wheel suspension", PartNameMatch.Uncertain)]
    [InlineData("Oil filter", "Air Filter", PartNameMatch.Uncertain)]
    [InlineData("Fuel filter", "Cabin Filter", PartNameMatch.Uncertain)]
    [InlineData("Oil filter", null, PartNameMatch.Uncertain)]
    public void Different_parts_are_still_caught(string invoice, string? part, string expected) =>
        Assert.Equal(expected, PartNameMatch.Verdict(invoice, part));
}
