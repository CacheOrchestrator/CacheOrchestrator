using CacheOrchestrator.Cluster;
using CacheOrchestrator.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CacheOrchestrator.HttpBus;

/// <summary>
/// Publishes cluster commands to peers over HTTP (<c>POST {base}{prefix}/cluster/apply</c>).
/// </summary>
internal sealed class HttpClusterCommandBus : IClusterCommandBus
{
    /// <summary>Named <see cref="HttpClient"/> for peer command delivery.</summary>
    public const string HttpClientName = "CacheOrchestrator.ClusterBus";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IClusterMembership _membership;
    private readonly IInstanceIdProvider _instanceId;
    private readonly IOptionsMonitor<HttpBusOptions> _options;
    private readonly ILogger<HttpClusterCommandBus> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpClusterCommandBus"/> class.
    /// </summary>
    public HttpClusterCommandBus(
        IHttpClientFactory httpClientFactory,
        IClusterMembership membership,
        IInstanceIdProvider instanceId,
        IOptionsMonitor<HttpBusOptions> options,
        ILogger<HttpClusterCommandBus> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _membership = membership;
        _instanceId = instanceId;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsEnabled => _options.CurrentValue.Cluster.Bus.Enabled;

    /// <inheritdoc />
    public async Task<ClusterPublishResult> PublishAsync(ClusterCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!IsEnabled)
            return ClusterPublishResult.Empty;

        HttpBusTransportOptions bus = _options.CurrentValue.Cluster.Bus;
        int timeoutMs = Math.Clamp(bus.PeerTimeoutMs, 100, 120_000);
        int parallelism = Math.Clamp(bus.MaxParallelism, 1, 64);

        IReadOnlyList<ClusterPeer> peers = await _membership.GetPeersAsync(cancellationToken).ConfigureAwait(false);
        string selfId = _instanceId.InstanceId;

        List<ClusterPeer> targets = [];
        foreach (ClusterPeer peer in peers)
        {
            if (string.Equals(peer.Id, selfId, StringComparison.OrdinalIgnoreCase))
                continue;
            targets.Add(peer);
        }

        if (targets.Count == 0)
        {
            _logger.LogDebug(
                "Cluster bus publish {CommandId}: no peers (membership={Kind})",
                command.CommandId,
                _membership.Kind);
            return ClusterPublishResult.Empty;
        }

        string routePrefix = ResolveRoutePrefix(_options.CurrentValue);
        string? apiKey = ResolveApiKey(_options.CurrentValue);
        HttpClient client = _httpClientFactory.CreateClient(HttpClientName);

        using SemaphoreSlim gate = new(parallelism, parallelism);
        var tasks = new Task<ClusterPeerPublishOutcome>[targets.Count];
        for (int i = 0; i < targets.Count; i++)
        {
            ClusterPeer peer = targets[i];
            tasks[i] = PublishToPeerAsync(client, peer, routePrefix, apiKey, command, timeoutMs, gate, cancellationToken);
        }

        ClusterPeerPublishOutcome[] outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new ClusterPublishResult(outcomes);
    }

    private async Task<ClusterPeerPublishOutcome> PublishToPeerAsync(
        HttpClient client,
        ClusterPeer peer,
        string routePrefix,
        string? apiKey,
        ClusterCommand command,
        int timeoutMs,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeoutMs);

            Uri applyUri = BuildApplyUri(peer.BaseUrl, routePrefix);
            using HttpRequestMessage request = new(HttpMethod.Post, applyUri);
            var envelope = ClusterCommandEnvelopeV1.FromCommand(command);
            request.Content = JsonContent.Create(envelope, options: JsonOptions);
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.TryAddWithoutValidation(ClusterEndpointAuth.HeaderName, apiKey);

            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new ClusterPeerPublishOutcome { PeerId = peer.Id, Succeeded = true };
            }

            CacheOrchestratorMetrics.RecordClusterPublishFailure("http_status");
            string error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
            _logger.LogWarning(
                "Cluster bus peer {PeerId} returned {StatusCode} for command {CommandId}",
                peer.Id,
                (int)response.StatusCode,
                command.CommandId);
            return new ClusterPeerPublishOutcome { PeerId = peer.Id, Succeeded = false, Error = error };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CacheOrchestratorMetrics.RecordClusterPublishFailure("timeout");
            string error = $"Timed out after {timeoutMs}ms";
            _logger.LogWarning(
                "Cluster bus peer {PeerId} timed out for command {CommandId} (timeoutMs={TimeoutMs})",
                peer.Id,
                command.CommandId,
                timeoutMs);
            return new ClusterPeerPublishOutcome { PeerId = peer.Id, Succeeded = false, Error = error };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CacheOrchestratorMetrics.RecordClusterPublishFailure("transport");
            _logger.LogWarning(
                ex,
                "Cluster bus peer {PeerId} failed for command {CommandId}",
                peer.Id,
                command.CommandId);
            return new ClusterPeerPublishOutcome { PeerId = peer.Id, Succeeded = false, Error = ex.Message };
        }
        finally
        {
            gate.Release();
        }
    }

    internal static Uri BuildApplyUri(Uri baseUrl, string routePrefix)
    {
        string baseStr = baseUrl.ToString().TrimEnd('/');
        string prefix = routePrefix.TrimEnd('/');
        if (!prefix.StartsWith('/'))
            prefix = "/" + prefix;
        return new Uri(baseStr + prefix + "/cluster/apply", UriKind.Absolute);
    }

    internal static string ResolveRoutePrefix(HttpBusOptions options)
    {
        string? prefix = options.Admin.RoutePrefix;
        return string.IsNullOrWhiteSpace(prefix) ? "/cache-admin/local" : prefix.TrimEnd('/');
    }

    internal static string? ResolveApiKey(HttpBusOptions options)
    {
        if (!string.IsNullOrEmpty(options.Cluster.Bus.ApiKey))
            return options.Cluster.Bus.ApiKey;
        if (!string.IsNullOrEmpty(options.Admin.ApiKey))
            return options.Admin.ApiKey;
        return null;
    }
}
