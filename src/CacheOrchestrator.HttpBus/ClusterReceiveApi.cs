using CacheOrchestrator.Cluster;
using CacheOrchestrator.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CacheOrchestrator.HttpBus;

/// <summary>
/// Maps cluster receive endpoints (independent of Admin API <c>Enabled</c>).
/// </summary>
internal static class ClusterReceiveApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Maps <c>.../cluster/apply</c> and <c>.../cluster/info</c> when the HTTP bus is registered
    /// and <c>Cache:Cluster:Bus:Enabled</c> is true. Safe no-op otherwise.
    /// </summary>
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        using IServiceScope scope = endpoints.ServiceProvider.CreateScope();
        HttpBusOptions opts = scope.ServiceProvider
            .GetRequiredService<IOptions<HttpBusOptions>>().Value;

        IClusterCommandBus bus = scope.ServiceProvider.GetRequiredService<IClusterCommandBus>();
        if (!opts.Cluster.Bus.Enabled || !bus.IsEnabled)
            return endpoints;

        string prefix = HttpClusterCommandBus.ResolveRoutePrefix(opts);

        if (string.IsNullOrEmpty(HttpClusterCommandBus.ResolveApiKey(opts))
            && opts.Cluster.Bus.AllowUnauthenticated)
        {
            ILogger? logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("CacheOrchestrator.HttpBus");
            logger?.LogWarning(
                "CacheOrchestrator cluster bus explicitly allows unauthenticated commands. " +
                "Receive endpoints at '{Prefix}/cluster' are open. " +
                "Use this only on an isolated development network.",
                prefix);
        }

        RouteGroupBuilder group = endpoints
            .MapGroup(prefix)
            .AddEndpointFilter<ClusterEndpointAuth>()
            .WithTags("CacheOrchestrator Cluster")
            .WithMetadata(new OutputCacheAttribute { NoStore = true });

        group.MapPost("/cluster/apply", async (
            HttpRequest request,
            IClusterCommandHandler handler,
            IInstanceIdProvider instanceId,
            IOptionsMonitor<CacheOrchestratorOptions> options,
            IOptionsMonitor<HttpBusOptions> busOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            ClusterCommand command;
            try
            {
                ClusterCommandEnvelopeV1? envelope = await JsonSerializer
                    .DeserializeAsync<ClusterCommandEnvelopeV1>(request.Body, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (envelope is null)
                    return Results.BadRequest(new { error = "Request body is required." });
                command = envelope.ToCommand();
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (string.IsNullOrWhiteSpace(command.OriginInstanceId)
                || string.IsNullOrWhiteSpace(command.Namespace))
            {
                return Results.BadRequest(new { error = "originInstanceId and namespace are required." });
            }

            HttpBusTransportOptions transport = busOptions.CurrentValue.Cluster.Bus;
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (command.TimestampUtc < now - TimeSpan.FromSeconds(transport.CommandMaxAgeSeconds))
            {
                return Results.BadRequest(new { error = "Command timestamp is too old." });
            }
            if (command.TimestampUtc > now + TimeSpan.FromSeconds(transport.ClockSkewSeconds))
            {
                return Results.BadRequest(new { error = "Command timestamp is too far in the future." });
            }

            string localNs = options.CurrentValue.Namespace ?? string.Empty;
            if (!string.Equals(command.Namespace, localNs, StringComparison.Ordinal))
            {
                return Results.Conflict(new
                {
                    error = "Namespace mismatch.",
                    commandNamespace = command.Namespace,
                    localNamespace = localNs
                });
            }

            if (string.Equals(command.OriginInstanceId, instanceId.InstanceId, StringComparison.Ordinal))
                return Results.Ok(new { applied = false, reason = "origin-is-self" });

            ClusterCommandResult result = await handler.ApplyLocalAsync(command, cancellationToken).ConfigureAwait(false);
            int statusCode = result.Status switch
            {
                ClusterCommandStatus.Rejected => StatusCodes.Status400BadRequest,
                ClusterCommandStatus.Failed => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status200OK
            };
            return Results.Json(new
            {
                applied = result.Status == ClusterCommandStatus.Applied,
                succeeded = result.Succeeded,
                status = result.Status.ToString(),
                reason = result.Reason,
                localMutationApplied = result.LocalMutationApplied,
                invalidation = result.Invalidation,
                errors = result.Errors,
                commandId = command.CommandId,
                commandType = command.GetType().Name
            }, statusCode: statusCode);
        });

        // When Admin API is enabled it already maps GET …/cluster/info (same prefix).
        // Skip a second registration to avoid ambiguous routes.
        if (!opts.Admin.Enabled)
        {
            group.MapGet("/cluster/info", async (
                IInstanceIdProvider instanceId,
                IClusterMembership membership,
                IClusterCommandBus busSvc,
                IOptionsMonitor<CacheOrchestratorOptions> options,
                CancellationToken cancellationToken) =>
            {
                IReadOnlyList<ClusterPeer> peers =
                    await membership.GetPeersAsync(cancellationToken).ConfigureAwait(false);

                return Results.Ok(new
                {
                    instanceId = instanceId.InstanceId,
                    @namespace = options.CurrentValue.Namespace,
                    busEnabled = busSvc.IsEnabled,
                    membership = membership.Kind,
                    peerCount = peers.Count,
                    peers = peers.Select(p => new { id = p.Id, url = p.BaseUrl.ToString() }).ToArray()
                });
            });
        }

        return endpoints;
    }
}
