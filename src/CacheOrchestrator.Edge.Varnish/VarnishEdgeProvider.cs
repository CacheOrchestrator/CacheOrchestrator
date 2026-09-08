using CacheOrchestrator.Edge.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Headers;

namespace CacheOrchestrator.Edge.Varnish;

internal sealed class VarnishEdgeProvider : IEdgeResponseProvider, IEdgeInvalidationProvider
{
    public const string ProviderName = "Varnish";
    public const string HttpClientName = "CacheOrchestrator.Edge.Varnish";
    internal const string TagHeader = "xkey";
    internal const string PurgeHeader = "xkey-purge";
    internal const string CacheableHeader = "X-CacheOrchestrator-Edge-Cacheable";
    internal const string TtlHeader = "X-CacheOrchestrator-Edge-Ttl";
    internal const string GraceHeader = "X-CacheOrchestrator-Edge-Grace";

    private readonly IHttpClientFactory _httpClientFactory;

    public VarnishEdgeProvider(
        IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    public string Name => ProviderName;

    public EdgeProviderCapabilities Capabilities { get; } = new()
    {
        SupportsTagInvalidation = true,
        MaxResponseTagBytes = 16 * 1024,
        MaxInvalidationBatchSize = 100,
        SupportsStaleWhileRevalidate = true,
        SupportsStaleIfError = false
    };

    public void ApplyResponseMetadata(HttpResponse response, EdgeResponseMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!metadata.IsCacheable)
        {
            response.Headers.Remove(TagHeader);
            response.Headers.Remove(TtlHeader);
            response.Headers.Remove(GraceHeader);
            response.Headers[CacheableHeader] = "0";
            return;
        }

        response.Headers[CacheableHeader] = "1";
        response.Headers[TtlHeader] = ToSeconds(metadata.Ttl).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (metadata.StaleWhileRevalidate is { } grace)
        {
            response.Headers[GraceHeader] = ToSeconds(grace).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            response.Headers.Remove(GraceHeader);
        }
        response.Headers[TagHeader] = string.Join(' ', metadata.Tags);
    }

    public EdgeInvalidationTarget CaptureTarget(string instanceName, IConfigurationSection instanceConfiguration)
    {
        string? url = instanceConfiguration["Varnish:PurgeUrl"];
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Varnish instance configuration is incomplete.");
        return new(instanceName, Name, uri.AbsoluteUri, new Dictionary<string, string>
        {
            ["purgeUrl"] = uri.AbsoluteUri,
            ["apiKey"] = instanceConfiguration["Varnish:ApiKey"] ?? string.Empty,
            ["apiKeyHeaderName"] = instanceConfiguration["Varnish:ApiKeyHeaderName"] ?? "X-CacheOrchestrator-Key"
        });
    }

    public async ValueTask<EdgeInvalidationResult> InvalidateAsync(
        EdgeInvalidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EdgeInvalidationTarget target = request.Target;
        if (!string.Equals(target.ProviderName, Name, StringComparison.OrdinalIgnoreCase) || target.FormatVersion != 1
            || !target.Parameters.TryGetValue("purgeUrl", out string? url)
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? purgeUri)
            || purgeUri.Scheme is not ("http" or "https"))
        {
            return new EdgeInvalidationResult { Error = "Varnish invalidation target is incomplete or unsupported." };
        }
        target.Parameters.TryGetValue("apiKey", out string? apiKey);
        target.Parameters.TryGetValue("apiKeyHeaderName", out string? apiKeyHeaderName);

        using var message = new HttpRequestMessage(new HttpMethod("PURGE"), purgeUri);
        if (!message.Headers.TryAddWithoutValidation(PurgeHeader, string.Join(' ', request.Tags)))
            return new EdgeInvalidationResult { Error = "Varnish invalidation tags could not be encoded as a header." };
        if (!string.IsNullOrEmpty(apiKey)
            && !message.Headers.TryAddWithoutValidation(apiKeyHeaderName ?? "X-CacheOrchestrator-Key", apiKey))
        {
            return new EdgeInvalidationResult { Error = "Varnish API-key header name is invalid." };
        }

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return EdgeInvalidationResult.Success;

            bool transient = response.StatusCode == HttpStatusCode.RequestTimeout
                || response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500;
            return new EdgeInvalidationResult
            {
                IsTransient = transient,
                RetryAfter = GetRetryAfter(response.Headers.RetryAfter),
                Error = $"Varnish PURGE returned HTTP {(int)response.StatusCode}."
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EdgeInvalidationResult { IsTransient = true, Error = "Varnish PURGE timed out." };
        }
        catch (HttpRequestException)
        {
            return new EdgeInvalidationResult { IsTransient = true, Error = "Varnish PURGE transport request failed." };
        }
    }

    private static long ToSeconds(TimeSpan value) => Math.Max(0, (long)value.TotalSeconds);

    private static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
            return delta;
        if (retryAfter?.Date is not { } date)
            return null;
        TimeSpan calculated = date - DateTimeOffset.UtcNow;
        return calculated > TimeSpan.Zero ? calculated : TimeSpan.Zero;
    }
}
