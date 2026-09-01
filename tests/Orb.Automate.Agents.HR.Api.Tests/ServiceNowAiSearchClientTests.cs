using System.Net;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orb.Automate.Agents.HR.Api.Diagnostics;
using Orb.Automate.Agents.HR.Api.Options;
using Orb.Automate.Agents.HR.Api.ServiceNow;

namespace Orb.Automate.Agents.HR.Api.Tests;

/// <summary>
/// Tests ServiceNow AI Search requests and response normalization.
/// </summary>
public sealed class ServiceNowAiSearchClientTests
{
    /// <summary>
    /// Mixed result tables are retained while markup is cleaned and result count is bounded.
    /// </summary>
    [Fact]
    public async Task SearchNormalizesMixedResults()
    {
        HttpRequestMessage? captured = null;
        var handler = new DelegateHttpMessageHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, SearchPayload));
        });
        var client = CreateClient(handler, new StubTokenProvider("token-value"), maxResults: 2);

        var response = await client.SearchAsync(
            "person+test@example.com",
            "absence holiday",
            CancellationToken.None);

        Assert.Equal(2, response.Count);
        Assert.Equal(2, response.Results.Count);
        Assert.Equal("kb_knowledge", response.Results[0].Table);
        Assert.Equal("Statutory Holiday & Pay", response.Results[0].Title);
        Assert.Equal("sc_cat_item", response.Results[1].Table);
        Assert.Equal("Bearer", captured?.Headers.Authorization?.Scheme);
        Assert.Equal("token-value", captured?.Headers.Authorization?.Parameter);
        Assert.Contains("upn=person%2Btest%40example.com", captured?.RequestUri?.Query, StringComparison.Ordinal);
        Assert.Contains("q=absence%20holiday", captured?.RequestUri?.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejected token is refreshed once before retrying the search.
    /// </summary>
    [Fact]
    public async Task UnauthorizedRefreshesTokenAndRetriesOnce()
    {
        var calls = 0;
        var handler = new DelegateHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : JsonResponse(HttpStatusCode.OK, EmptyPayload));
        });
        var tokens = new StubTokenProvider("token-1", "token-2");
        var client = CreateClient(handler, tokens);

        var response = await client.SearchAsync("person@example.com", "leave", CancellationToken.None);

        Assert.Empty(response.Results);
        Assert.Equal(2, calls);
        Assert.Equal(1, tokens.Invalidations);
    }

    /// <summary>
    /// Payload-level failures are rejected even when HTTP succeeds.
    /// </summary>
    [Fact]
    public async Task NonSuccessPayloadStatusThrowsControlledFailure()
    {
        var handler = new DelegateHttpMessageHandler((_, _) => Task.FromResult(
            JsonResponse(HttpStatusCode.OK, "{\"result\":{\"status\":500,\"msg\":\"FAIL\"}}")));
        var client = CreateClient(handler, new StubTokenProvider("token"));

        var exception = await Assert.ThrowsAsync<ServiceNowException>(() =>
            client.SearchAsync("person@example.com", "leave", CancellationToken.None));

        Assert.Equal(ServiceNowFailureKind.InvalidResponse, exception.Kind);
    }

    private static ServiceNowAiSearchClient CreateClient(
        HttpMessageHandler handler,
        IServiceNowOAuthTokenProvider tokenProvider,
        int maxResults = 12) =>
        new(
            new StubHttpClientFactory(handler),
            tokenProvider,
            Microsoft.Extensions.Options.Options.Create(new ServiceNowOptions
            {
                Enabled = true,
                Endpoint = "https://example.service-now.com",
                TokenEndpoint = "https://example.service-now.com/oauth_token.do",
                ClientId = "client-id",
                ClientSecret = "client-secret",
                MaxResults = maxResults
                }),
                new DevelopmentPayloadWriter(new NonDevelopmentEnvironment()),
                NullLogger<ServiceNowAiSearchClient>.Instance);

            private sealed class NonDevelopmentEnvironment : IHostEnvironment
            {
              public string EnvironmentName { get; set; } = Environments.Production;

              public string ApplicationName { get; set; } = "Tests";

              public string ContentRootPath { get; set; } = Path.GetTempPath();

              public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
            }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubTokenProvider(params string[] tokens) : IServiceNowOAuthTokenProvider
    {
        private int _index;

        public int Invalidations { get; private set; }

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            var token = tokens[Math.Min(_index, tokens.Length - 1)];
            _index++;
            return Task.FromResult(token);
        }

        public void Invalidate() => Invalidations++;
    }

    private const string EmptyPayload = """
        {"result":{"status":200,"data":{"search":{"term":"leave","searchResults":[],"count":0}}}}
        """;

    private const string SearchPayload = """
        {
          "result": {
            "status": 200,
            "data": {
              "search": {
                "term": "absence holiday",
                "count": 2.0,
                "searchResults": [
                  {
                    "sysId": "kb1",
                    "table": "kb_knowledge",
                    "title": "Statutory <highlight>Holiday</highlight> &amp; Pay",
                    "text": "Approved <highlight>holiday</highlight> guidance.",
                    "columns": [
                      {"fieldName":"number","label":"Number","value":"KB001","displayValue":"KB001"}
                    ]
                  },
                  {
                    "sysId": "cat1",
                    "table": "sc_cat_item",
                    "title": "Leave Request",
                    "text": "Request leave.",
                    "columns": []
                  }
                ]
              }
            }
          }
        }
        """;
}
