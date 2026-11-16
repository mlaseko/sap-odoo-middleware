namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

/// <summary>Model of the invoice tab bar (_InvoiceTabs): which invoice, and which tab is open (lines / issues / prices / finish / history).</summary>
public sealed record InvoiceTabs(Guid DocumentId, string Active);
