using Cove.Api.Controllers;
using Microsoft.AspNetCore.Http;

namespace Cove.Tests;

public sealed class UsersControllerTests
{
    [Fact]
    public void Invite_base_url_uses_the_browser_origin_for_reverse_proxied_requests()
    {
        var request = CreateRequest("http", "cove.internal:5073");
        request.Headers.Origin = "https://cove.example.test";

        var baseUrl = UsersController.ResolveInviteBaseUrl(request);

        Assert.Equal("https://cove.example.test", baseUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("https://cove.example.test/path")]
    [InlineData("ftp://cove.example.test")]
    public void Invite_base_url_falls_back_to_the_request_origin_when_the_origin_header_is_unusable(string? origin)
    {
        var request = CreateRequest("http", "cove.internal:5073");
        if (origin is not null)
            request.Headers.Origin = origin;

        var baseUrl = UsersController.ResolveInviteBaseUrl(request);

        Assert.Equal("http://cove.internal:5073", baseUrl);
    }

    private static HttpRequest CreateRequest(string scheme, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        return context.Request;
    }
}
