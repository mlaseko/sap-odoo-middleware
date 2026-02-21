using SapOdooMiddleware.Models.Api;

namespace SapOdooMiddleware.Tests;

public class ApiResponseTests
{
    [Fact]
    public void Ok_ReturnsSuccessTrue_WithData()
    {
        var response = ApiResponse<string>.Ok("hello");

        Assert.True(response.Success);
        Assert.Equal("hello", response.Data);
        Assert.Null(response.Errors);
    }

    [Fact]
    public void Ok_WithMeta_ReturnsMeta()
    {
        var meta = new Dictionary<string, object> { ["page"] = 1 };
        var response = ApiResponse<int>.Ok(42, meta);

        Assert.True(response.Success);
        Assert.Equal(42, response.Data);
        Assert.NotNull(response.Meta);
        Assert.Equal(1, response.Meta["page"]);
    }

    [Fact]
    public void Fail_SingleError_ReturnsSuccessFalse()
    {
        var response = ApiResponse<object>.Fail("Something went wrong");

        Assert.False(response.Success);
        Assert.Single(response.Errors!);
        Assert.Equal("Something went wrong", response.Errors![0]);
    }

    [Fact]
    public void Fail_MultipleErrors_ReturnsAllErrors()
    {
        var response = ApiResponse<object>.Fail(["Error 1", "Error 2"]);

        Assert.False(response.Success);
        Assert.Equal(2, response.Errors!.Count);
    }
}
