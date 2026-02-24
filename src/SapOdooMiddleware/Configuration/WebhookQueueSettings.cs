namespace SapOdooMiddleware.Configuration;

public class WebhookQueueSettings
{
    public const string SectionName = "WebhookQueue";
    public string ConnectionString { get; set; } = string.Empty;
    public int PollingIntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 10;
    public int MaxRetries { get; set; } = 5;
    public bool Enabled { get; set; } = true;
}
