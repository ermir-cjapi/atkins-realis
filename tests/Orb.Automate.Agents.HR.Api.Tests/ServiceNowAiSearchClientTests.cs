using System.Net;
using System.Diagnostics;
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
  /// A fresh successful response, including an empty result, avoids another outbound request.
  /// </summary>
  [Fact]
  public async Task FreshEmptyResultAvoidsHttp()
  {
    var calls = 0;
    var handler = new DelegateHttpMessageHandler((_, _) =>
    {
      calls++;
      return Task.FromResult(JsonResponse(HttpStatusCode.OK, EmptyPayload));
    });
    var client = CreateClient(handler, new StubTokenProvider("token"));

    var first = await client.SearchAsync("person@example.com", "leave", CancellationToken.None);
    var second = await client.SearchAsync("person@example.com", "leave", CancellationToken.None);

    Assert.Empty(first.Results);
    Assert.Equal(ServiceNowSearchSource.FreshCache, second.Source);
    Assert.Equal(1, calls);
  }

  /// <summary>
  /// Cache scope normalizes casing and whitespace but remains isolated between users.
  /// </summary>
  [Fact]
  public async Task CacheNormalizesInputsAndIsolatesUsers()
  {
    var calls = 0;
    var handler = new DelegateHttpMessageHandler((_, _) =>
    {
      calls++;
      return Task.FromResult(JsonResponse(HttpStatusCode.OK, EmptyPayload));
    });
    var client = CreateClient(handler, new StubTokenProvider("token"));

    await client.SearchAsync(" Person@Example.com ", " leave ", CancellationToken.None);
    await client.SearchAsync("person@example.com", "LEAVE", CancellationToken.None);
    await client.SearchAsync("other@example.com", "leave", CancellationToken.None);

    Assert.Equal(2, calls);
  }

  /// <summary>
  /// Expired fresh data is returned as stale after live failure, but never beyond the maximum stale age.
  /// </summary>
  [Fact]
  public async Task StaleFallbackIsBounded()
  {
    var calls = 0;
    var time = new AdjustableTimeProvider();
    var handler = new DelegateHttpMessageHandler((_, _) =>
    {
      calls++;
      return Task.FromResult(calls == 1
        ? JsonResponse(HttpStatusCode.OK, SearchPayload)
        : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    });
    var client = CreateClient(handler, new StubTokenProvider("token"), timeProvider: time);

    await client.SearchAsync("person@example.com", "leave", CancellationToken.None);
    time.Advance(TimeSpan.FromMinutes(16));
    var stale = await client.SearchAsync("person@example.com", "leave", CancellationToken.None);
    time.Advance(TimeSpan.FromMinutes(45));
    var exception = await Assert.ThrowsAsync<ServiceNowException>(() =>
      client.SearchAsync("person@example.com", "leave", CancellationToken.None));

    Assert.Equal(ServiceNowSearchSource.StaleCache, stale.Source);
    Assert.Equal(ServiceNowFailureKind.Unavailable, exception.Kind);
    Assert.Equal(3, calls);
  }

  /// <summary>
  /// Failures are not cached and concurrent misses are coalesced into one live call.
  /// </summary>
  [Fact]
  public async Task ConcurrentMissesAreCoalescedAndFailuresAreNotCached()
  {
    var calls = 0;
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var handler = new DelegateHttpMessageHandler(async (_, cancellationToken) =>
    {
      Interlocked.Increment(ref calls);
      await release.Task.WaitAsync(cancellationToken);
      return JsonResponse(HttpStatusCode.OK, EmptyPayload);
    });
    var client = CreateClient(handler, new StubTokenProvider("token"));
    var searches = Enumerable.Range(0, 10)
      .Select(_ => client.SearchAsync("person@example.com", "leave", CancellationToken.None))
      .ToArray();
    release.SetResult();
    await Task.WhenAll(searches);

    Assert.Equal(1, calls);

    calls = 0;
    var failing = CreateClient(
      new DelegateHttpMessageHandler((_, _) =>
      {
        calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
      }),
      new StubTokenProvider("token"));
    await Assert.ThrowsAsync<ServiceNowException>(() => failing.SearchAsync("person@example.com", "leave", CancellationToken.None));
    await Assert.ThrowsAsync<ServiceNowException>(() => failing.SearchAsync("person@example.com", "leave", CancellationToken.None));
    Assert.Equal(2, calls);
  }

  /// <summary>
  /// One turn deadline bounds outbound work and is not renewed by a repeated tool invocation.
  /// </summary>
  [Fact]
  public async Task AggregateBudgetCancelsOutboundWorkAndDoesNotRenew()
  {
    var calls = 0;
    var handler = new DelegateHttpMessageHandler(async (_, cancellationToken) =>
    {
      calls++;
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      return JsonResponse(HttpStatusCode.OK, EmptyPayload);
    });
    var settings = Microsoft.Extensions.Options.Options.Create(new ServiceNowOptions
    {
      Enabled = true,
      Endpoint = "https://example.service-now.com",
      TokenEndpoint = "https://example.service-now.com/oauth_token.do",
      ClientId = "client-id",
      ClientSecret = "client-secret",
      TurnBudgetMilliseconds = 1_000
    });
    var turnAccessor = new ServiceNowTurnContextAccessor(settings, TimeProvider.System);
    var client = new ServiceNowAiSearchClient(
      new StubHttpClientFactory(handler),
      new StubTokenProvider("token"),
      new ServiceNowResultCache(settings, TimeProvider.System),
      turnAccessor,
      TimeProvider.System,
      settings,
      new DevelopmentPayloadWriter(new NonDevelopmentEnvironment()));

    using var turn = turnAccessor.BeginTurn();
    var stopwatch = Stopwatch.StartNew();
    var first = await Assert.ThrowsAsync<ServiceNowException>(() =>
      client.SearchAsync("person@example.com", "leave", CancellationToken.None));
    var second = await Assert.ThrowsAsync<ServiceNowException>(() =>
      client.SearchAsync("person@example.com", "leave", CancellationToken.None));

    Assert.Equal(ServiceNowFailureKind.BudgetExpired, first.Kind);
    Assert.Equal(ServiceNowFailureKind.BudgetExpired, second.Kind);
    Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(2));
    Assert.Equal(1, calls);
  }

  /// <summary>
  /// Time spent in the turn before the first ServiceNow call does not consume the ServiceNow budget.
  /// </summary>
  [Fact]
  public async Task BudgetStartsAtFirstServiceNowCall()
  {
    var time = new AdjustableTimeProvider();
    var handler = new DelegateHttpMessageHandler((_, _) =>
      Task.FromResult(JsonResponse(HttpStatusCode.OK, EmptyPayload)));
    var settings = Microsoft.Extensions.Options.Options.Create(new ServiceNowOptions
    {
      Enabled = true,
      Endpoint = "https://example.service-now.com",
      TokenEndpoint = "https://example.service-now.com/oauth_token.do",
      ClientId = "client-id",
      ClientSecret = "client-secret",
      TurnBudgetMilliseconds = 1_000
    });
    var turnAccessor = new ServiceNowTurnContextAccessor(settings, time);
    var client = new ServiceNowAiSearchClient(
      new StubHttpClientFactory(handler),
      new StubTokenProvider("token"),
      new ServiceNowResultCache(settings, time),
      turnAccessor,
      time,
      settings,
      new DevelopmentPayloadWriter(new NonDevelopmentEnvironment()));

    using var turn = turnAccessor.BeginTurn();
    time.Advance(TimeSpan.FromSeconds(20));

    Assert.False(turnAccessor.Current!.IsExpired);
    var response = await client.SearchAsync("person@example.com", "leave", CancellationToken.None);

    Assert.Equal(ServiceNowSearchSource.Live, response.Source);
  }

  /// <summary>
  /// Caller cancellation propagates as cancellation rather than being reported as budget expiry.
  /// </summary>
  [Fact]
  public async Task CallerCancellationRemainsDistinct()
  {
    var handler = new DelegateHttpMessageHandler(async (_, cancellationToken) =>
    {
      await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      return JsonResponse(HttpStatusCode.OK, EmptyPayload);
    });
    var client = CreateClient(handler, new StubTokenProvider("token"));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
      client.SearchAsync("person@example.com", "leave", cancellation.Token));
  }

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

      /// <summary>
      /// A remote throttle is not retried by the client and preserves its bounded Retry-After signal.
      /// </summary>
      [Fact]
      public async Task RemoteThrottleCapturesRetryAfter()
      {
        var calls = 0;
        var handler = new DelegateHttpMessageHandler((_, _) =>
        {
          calls++;
          var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
          response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
          return Task.FromResult(response);
        });
        var client = CreateClient(handler, new StubTokenProvider("token"));

        var exception = await Assert.ThrowsAsync<ServiceNowException>(() =>
          client.SearchAsync("person@example.com", "leave", CancellationToken.None));

        Assert.Equal(ServiceNowFailureKind.RemoteThrottled, exception.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), exception.RetryAfter);
        Assert.Equal(1, calls);
      }

    private static ServiceNowAiSearchClient CreateClient(
        HttpMessageHandler handler,
        IServiceNowOAuthTokenProvider tokenProvider,
      int maxResults = 12,
      TimeProvider? timeProvider = null)
    {
      timeProvider ??= TimeProvider.System;
      var configuredOptions = Microsoft.Extensions.Options.Options.Create(new ServiceNowOptions
      {
        Enabled = true,
        Endpoint = "https://example.service-now.com",
        TokenEndpoint = "https://example.service-now.com/oauth_token.do",
        ClientId = "client-id",
        ClientSecret = "client-secret",
        MaxResults = maxResults
      });
      return new(
            new StubHttpClientFactory(handler),
            tokenProvider,
            new ServiceNowResultCache(configuredOptions, timeProvider),
            new ServiceNowTurnContextAccessor(configuredOptions, timeProvider),
            timeProvider,
        configuredOptions,
        new DevelopmentPayloadWriter(new NonDevelopmentEnvironment()));
    }

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

    private sealed class AdjustableTimeProvider : TimeProvider
    {
      private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
      private long _timestamp;

      public override DateTimeOffset GetUtcNow() => _utcNow;

      public override long GetTimestamp() => _timestamp;

      public override long TimestampFrequency => TimeSpan.TicksPerSecond;

      public void Advance(TimeSpan duration)
      {
        _utcNow += duration;
        _timestamp += duration.Ticks;
      }
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
