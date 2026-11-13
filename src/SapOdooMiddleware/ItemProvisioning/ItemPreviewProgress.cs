using MolasLubes.Infrastructure.Integrations.LiquiMoly;

namespace SapOdooMiddleware.ItemProvisioning;

/// <summary>One step of an inquiry preview, as the Odoo panel lists it.</summary>
public record ItemPreviewStep(
    string Key,
    string Label,
    string State,                 // "pending" | "running" | "done" | "skipped" | "failed"
    string? Detail,
    double? Seconds);

/// <summary>
/// Live progress of <see cref="ILubesItemProvisioningService.PreviewAsync"/>: each step as it starts and ends, and the
/// scraped product as soon as it is known (so the user sees the item while it is still being classified).
/// </summary>
public interface IItemPreviewProgress
{
    void Step(string key, string label, string state, string? detail = null);
    void Product(LiquiMolyProductDto product, string brand, string itemName);
}

/// <summary>No-op progress for callers that do not track it (tests, direct calls).</summary>
public sealed class NullItemPreviewProgress : IItemPreviewProgress
{
    public static readonly NullItemPreviewProgress Instance = new();
    public void Step(string key, string label, string state, string? detail = null) { }
    public void Product(LiquiMolyProductDto product, string brand, string itemName) { }
}
