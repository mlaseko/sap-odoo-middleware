namespace SapOdooMiddleware.Services;

/// <summary>
/// Marker for the SAP B1 DI-API connection bound to the <b>Autohub</b> company
/// (<c>Companies:Autohub:SapB1</c>) — a second, independent persistent connection distinct from the
/// default <see cref="ISapB1Service"/> (which serves the Lubes / top-level company).
///
/// Autohub spare-parts item creation resolves <em>this</em> service so items land in the Autohub
/// company (e.g. "Molas Live 2021"), not the Lubes company. It uses its own license seat and its own
/// lock, so Lubes and Autohub SAP work do not contend.
/// </summary>
public interface IAutohubSapB1Service : ISapB1Service
{
}
