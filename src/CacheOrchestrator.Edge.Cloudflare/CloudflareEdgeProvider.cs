using CacheOrchestrator.Edge.Providers;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace CacheOrchestrator.Edge.Cloudflare;

internal sealed class CloudflareEdgeProvider : IEdgeResponseProvider, IEdgeInvalidationProvider
{
    public const string ProviderName = "Cloudflare";
    public const string HttpClientName = "CacheOrchestrator.Edge.Cloudflare";
    private const string TagHeader = "Cache-Tag";
    private const string CacheControlHeader = "Cloudflare-CDN-Cache-Control";

    private readonly IHttpClientFactory _httpClientFactory;

    public CloudflareEdgeProvider(
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
        SupportsStaleIfError = true
    };

    public void ApplyResponseMetadata(
        Microsoft.AspNetCore.Http.HttpResponse response,
        EdgeResponseMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!metadata.IsCacheable)
        {
            response.Headers.Remove(TagHeader);
            response.Headers[CacheControlHeader] = "no-store";
            return;
        }

        List<string> directives = [$"max-age={ToSeconds(metadata.Ttl)}"];
        if (metadata.StaleWhileRevalidate is { } staleWhileRevalidate)
            directives.Add($"stale-while-revalidate={ToSeconds(staleWhileRevalidate)}");
        if (metadata.StaleIfError is { } staleIfError)
            directives.Add($"stale-if-error={ToSeconds(staleIfError)}");

        response.Headers[CacheControlHeader] = string.Join(", ", directives);
        response.Headers[TagHeader] = string.Join(',', metadata.Tags);
    }

    public EdgeInvalidationTarget CaptureTarget(string instanceName, IConfigurationSection instanceConfiguration)
    {
        string? zone = instanceConfiguration["Cloudflare:ZoneId"];
        string? token = instanceConfiguration["Cloudflare:ApiToken"];
        if (string.IsNullOrWhiteSpace(zone) || string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Cloudflare instance configuration is incomplete.");
        return new(instanceName, Name, zone, new Dictionary<string, string> { ["zoneId"] = zone, ["apiToken"] = token });
    }

    public async ValueTask<EdgeInvalidationResult> InvalidateAsync(
        EdgeInvalidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EdgeInvalidationTarget target = request.Target;
        if (!string.Equals(target.ProviderName, Name, StringComparison.OrdinalIgnoreCase)
            || target.FormatVersion != 1
            || !target.Parameters.TryGetValue("zoneId", out string? zone)
            || !target.Parameters.TryGetValue("apiToken", out string? token)
            || string.IsNullOrWhiteSpace(zone) || string.IsNullOrWhiteSpace(token))
        {
            return new EdgeInvalidationResult { Error = "Cloudflare invalidation target is incomplete or unsupported." };
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"zones/{Uri.EscapeDataString(zone)}/purge_cache");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Content = JsonContent.Create(new { tags = request.Tags });

        try
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            TimeSpan? retryAfter = GetRetryAfter(response.Headers.RetryAfter);
            bool transient = response.StatusCode == HttpStatusCode.RequestTimeout
                || response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500;

            if (!response.IsSuccessStatusCode)
            {
                return new EdgeInvalidationResult
                {
                    IsTransient = transient,
                    RetryAfter = retryAfter,
                    Error = $"Cloudflare API returned HTTP {(int)response.StatusCode}."
                };
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("success", out JsonElement success)
                && success.ValueKind == JsonValueKind.True)
            {
                return EdgeInvalidationResult.Success;
            }

            return new EdgeInvalidationResult { Error = "Cloudflare rejected the purge request." };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new EdgeInvalidationResult { IsTransient = true, Error = "Cloudflare request timed out." };
        }
        catch (HttpRequestException)
        {
            return new EdgeInvalidationResult { IsTransient = true, Error = "Cloudflare transport request failed." };
        }
        catch (JsonException)
        {
            return new EdgeInvalidationResult { Error = "Cloudflare returned an invalid response." };
        }
    }

    private static long ToSeconds(TimeSpan value) => Math.Max(0, (long)value.TotalSeconds);

    private static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }
        if (retryAfter?.Date is { } date)
        {
            TimeSpan calculated = date - DateTimeOffset.UtcNow;
            return calculated > TimeSpan.Zero
                ? calculated
                : TimeSpan.Zero;
        }

        return null;
    }
}
