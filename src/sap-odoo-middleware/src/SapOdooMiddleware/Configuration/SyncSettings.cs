namespace SapOdooMiddleware.Configuration;

public class SyncSettings
{
    public int ProductIntervalMinutes { get; set; } = 15;
    public int StockIntervalMinutes { get; set; } = 5;
    public int PriceIntervalMinutes { get; set; } = 30;
    public int PartnerIntervalMinutes { get; set; } = 60;
    public int QueueProcessorIntervalMinutes { get; set; } = 1;
    public int QueueBatchSize { get; set; } = 50;
    public int RetryBaseDelaySeconds { get; set; } = 60;
    public int RetryMaxDelaySeconds { get; set; } = 3600;
    public int MaxRetryAttempts { get; set; } = 5;
    public int DefaultPageSize { get; set; } = 50;
}
