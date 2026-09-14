using Microsoft.AspNetCore.Http;
using Orb.Automate.Agents.HR.Api.Auth;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests how the inbound user assertion is read from a request. A defect here silently disables
/// permission trimming, so the accepted and rejected shapes are both pinned.
/// </summary>
public sealed class SearchUserTokenProviderTests
{
    /// <summary>
    /// Verifies that no identity is reported when there is no request in scope.
    /// </summary>
    [Fact]
    public void ReadBearerToken_ReturnsNullWhenThereIsNoRequest() =>
        Assert.Null(SearchUserTokenProvider.ReadBearerToken(null));

    /// <summary>
    /// Verifies that a bearer token is extracted regardless of scheme casing or padding.
    /// </summary>
    /// <param name="header">The inbound header value.</param>
    /// <param name="expected">The expected token.</param>
    [Theory]
    [InlineData("Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("Bearer   abc.def.ghi  ", "abc.def.ghi")]
    public void ReadBearerToken_ExtractsTheToken(string header, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = header;

        Assert.Equal(expected, SearchUserTokenProvider.ReadBearerToken(context));
    }

    /// <summary>
    /// Verifies that headers which cannot yield a bearer token are rejected rather than forwarded.
    /// </summary>
    /// <param name="header">The inbound header value.</param>
    [Theory]
    [InlineData("")]
    [InlineData("Basic dXNlcjpwYXNzd29yZA==")]
    [InlineData("Bearer")]
    [InlineData("Bearer   ")]
    public void ReadBearerToken_RejectsUnusableHeaders(string header)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = header;

        Assert.Null(SearchUserTokenProvider.ReadBearerToken(context));
    }
}
