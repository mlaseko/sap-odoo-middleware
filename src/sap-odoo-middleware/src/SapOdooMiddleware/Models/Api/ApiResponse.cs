using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Models.Api;

public class ApiResponse<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("meta")]
    public ApiMeta? Meta { get; set; }

    [JsonPropertyName("errors")]
    public List<ApiError> Errors { get; set; } = new();

    public static ApiResponse<T> Ok(T data, ApiMeta? meta = null)
    {
        return new ApiResponse<T> { Success = true, Data = data, Meta = meta };
    }

    public static ApiResponse<T> Fail(string code, string message, string? detail = null)
    {
        return new ApiResponse<T>
        {
            Success = false,
            Errors = new List<ApiError> { new(code, message, detail) }
        };
    }
}

public class ApiMeta
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("page")]
    public int? Page { get; set; }

    [JsonPropertyName("page_size")]
    public int? PageSize { get; set; }

    [JsonPropertyName("total_count")]
    public int? TotalCount { get; set; }
}

public class ApiError
{
    [JsonPropertyName("code")]
    public string Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    public ApiError(string code, string message, string? detail = null)
    {
        Code = code;
        Message = message;
        Detail = detail;
    }
}
