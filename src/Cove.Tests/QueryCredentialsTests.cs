using Cove.Api.Http;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Cove.Tests;

/// <summary>
/// Locks in credential propagation across same-origin API redirects: a request authenticated via
/// query string (<c>access_token</c> / <c>share_token</c> / <c>share_password</c>) must keep those
/// credentials when an endpoint redirects to another API URL, or clients without a session cookie
/// (img elements on share links, API consumers) get a 401 on the redirect hop.
/// </summary>
public class QueryCredentialsTests
{
    private static HttpRequest RequestWithQuery(string query)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString(query);
        return ctx.Request;
    }

    [Fact]
    public void AccessToken_IsCarriedToRedirectUrl()
    {
        var request = RequestWithQuery("?max=640&access_token=tok123");
        Assert.Equal("/api/stream/video/1/screenshot?access_token=tok123",
            QueryCredentials.Preserve(request, "/api/stream/video/1/screenshot"));
    }

    [Fact]
    public void ExistingQuery_IsAppendedWithAmpersand()
    {
        var request = RequestWithQuery("?access_token=tok123");
        Assert.Equal("/api/stream/image/2/thumbnail?max=640&access_token=tok123",
            QueryCredentials.Preserve(request, "/api/stream/image/2/thumbnail?max=640"));
    }

    [Fact]
    public void ShareCredentials_AreCarriedTogether()
    {
        var request = RequestWithQuery("?share_token=st&share_password=sp");
        Assert.Equal("/api/stream/video/3/screenshot?share_token=st&share_password=sp",
            QueryCredentials.Preserve(request, "/api/stream/video/3/screenshot"));
    }

    [Fact]
    public void NoCredentials_ReturnsUrlUnchanged()
    {
        var request = RequestWithQuery("?max=640&v=2026");
        Assert.Equal("/api/stream/video/4/screenshot",
            QueryCredentials.Preserve(request, "/api/stream/video/4/screenshot"));
    }

    [Fact]
    public void CredentialValues_AreUrlEscaped()
    {
        var request = RequestWithQuery("?access_token=" + Uri.EscapeDataString("a+b/c="));
        Assert.Equal("/x?access_token=a%2Bb%2Fc%3D", QueryCredentials.Preserve(request, "/x"));
    }
}
