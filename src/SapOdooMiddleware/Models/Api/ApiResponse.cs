namespace SapOdooMiddleware.Models.Api;

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public Dictionary<string, object>? Meta { get; set; }
    public List<string>? Errors { get; set; }

    public static ApiResponse<T> Ok(T data, Dictionary<string, object>? meta = null) =>
        new() { Success = true, Data = data, Meta = meta };

    public static ApiResponse<T> Fail(List<string> errors) =>
        new() { Success = false, Errors = errors };

    public static ApiResponse<T> Fail(string error) =>
        new() { Success = false, Errors = [error] };
}
