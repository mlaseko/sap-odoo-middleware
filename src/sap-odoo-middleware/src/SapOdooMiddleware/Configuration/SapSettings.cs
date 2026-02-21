namespace SapOdooMiddleware.Configuration;

public class SapSettings
{
    public string SqlConnectionString { get; set; } = string.Empty;
    public string ServiceLayerUrl { get; set; } = "https://localhost:50000/b1s/v1/";
    public string ServiceLayerCompanyDb { get; set; } = string.Empty;
    public string ServiceLayerUser { get; set; } = string.Empty;
    public string ServiceLayerPassword { get; set; } = string.Empty;
}
