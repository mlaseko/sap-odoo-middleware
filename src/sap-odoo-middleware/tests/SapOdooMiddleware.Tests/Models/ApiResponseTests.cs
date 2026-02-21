using SapOdooMiddleware.Models.Api;

namespace SapOdooMiddleware.Tests.Models;

public class ApiResponseTests
{
    [Fact]
    public void Ok_CreatesSuccessfulResponse()
    {
        var response = ApiResponse<string>.Ok("test-data");

        Assert.True(response.Success);
        Assert.Equal("test-data", response.Data);
        Assert.Empty(response.Errors);
    }

    [Fact]
    public void Ok_WithMeta_IncludesMetadata()
    {
        var meta = new ApiMeta { Page = 1, PageSize = 50, TotalCount = 100 };
        var response = ApiResponse<string>.Ok("test", meta);

        Assert.True(response.Success);
        Assert.NotNull(response.Meta);
        Assert.Equal(1, response.Meta.Page);
        Assert.Equal(50, response.Meta.PageSize);
        Assert.Equal(100, response.Meta.TotalCount);
    }

    [Fact]
    public void Fail_CreatesFailedResponse()
    {
        var response = ApiResponse<string>.Fail("TEST_ERROR", "Something went wrong", "details here");

        Assert.False(response.Success);
        Assert.Null(response.Data);
        Assert.Single(response.Errors);
        Assert.Equal("TEST_ERROR", response.Errors[0].Code);
        Assert.Equal("Something went wrong", response.Errors[0].Message);
        Assert.Equal("details here", response.Errors[0].Detail);
    }

    [Fact]
    public void Fail_WithoutDetail_HasNullDetail()
    {
        var response = ApiResponse<string>.Fail("ERR", "msg");

        Assert.Null(response.Errors[0].Detail);
    }
}
