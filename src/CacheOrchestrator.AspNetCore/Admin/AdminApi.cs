using CacheOrchestrator.Cluster;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Invalidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CacheOrchestrator.Admin;

/// <summary>
/// Maps Admin API endpoints (per instance). No-op when Admin is disabled.
/// Prefer <see cref="DependencyInjection.ApplicationBuilderExtensions.MapCacheOrchestratorAdmin"/>.
/// </summary>
public static class AdminApi
{
    /// <summary>
    /// Maps Admin API routes under <c>Cache:Admin:RoutePrefix</c> when Admin is enabled.
    /// Safe to call when Admin is disabled (maps nothing).
    /// </summary>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <returns>The same <paramref name="endpoints"/> for chaining.</returns>
    public static IEndpointRouteBuilder Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        using IServiceScope scope = endpoints.ServiceProvider.CreateScope();
        CacheOrchestratorHttpOptions opts = scope.ServiceProvider
            .GetRequiredService<IOptions<CacheOrchestratorHttpOptions>>().Value;

        if (!opts.Admin.Enabled)
            return endpoints;

        string prefix = string.IsNullOrWhiteSpace(opts.Admin.RoutePrefix)
            ? "/cache-admin/local"
            : opts.Admin.RoutePrefix.TrimEnd('/');

        if (string.IsNullOrEmpty(opts.Admin.ApiKey))
        {
            ILogger? logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()
                ?.CreateLogger("CacheOrchestrator.Admin");
            logger?.LogWarning(
                "CacheOrchestrator Admin is enabled without ApiKey. Admin API at '{Prefix}' is open. " +
                "Set Cache:Admin:ApiKey for non-local environments.",
                prefix);
        }

        // Admin is operational traffic — never store responses in Output Cache.
        RouteGroupBuilder group = endpoints
            .MapGroup(prefix)
            .AddEndpointFilter<AdminApiKeyEndpointFilter>()
            .WithTags("CacheOrchestrator Admin")
            .WithMetadata(new OutputCacheAttribute { NoStore = true });

        group.MapGet("/health", async (
            ICacheOrchestratorManagement management,
            CancellationToken cancellationToken) =>
            Results.Ok(await management.GetHealthAsync(cancellationToken).ConfigureAwait(false)));

        // Always available when Admin API is on (even without CacheOrchestrator.HttpBus).
        // Prevents SPA MapFallbackToFile HTML from being mistaken for JSON on probe misses.
        group.MapGet("/cluster/info", async (
            ICacheOrchestratorManagement management,
            CancellationToken cancellationToken) =>
            Results.Ok(await management.GetClusterInfoAsync(cancellationToken).ConfigureAwait(false)));

        // Process-lifetime raw counters. Prefer OTEL/Prometheus for time-window analytics.
        // Kept for external tools; Admin Console stats UI uses Prometheus only.
        group.MapGet("/stats", (ICacheOrchestratorManagement management) => Results.Ok(management.GetStats()));

        group.MapGet("/endpoints", (ICacheOrchestratorManagement management) =>
            Results.Ok(management.GetEndpoints()));

        group.MapGet("/domains", (ICacheOrchestratorManagement management) =>
            Results.Ok(management.GetDomains()));

        group.MapGet("/domains/{domain}", (string domain, ICacheOrchestratorManagement management) =>
        {
            AdminDomainConfigDto? dto = management.GetDomain(domain);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        });

        group.MapPost("/invalidate", async (
            AdminInvalidateRequest? body,
            ICacheOrchestratorManagement management,
            CancellationToken cancellationToken) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = "Request body is required." });

            try
            {
                CacheInvalidationResult result = await management.InvalidateAsync(body, cancellationToken)
                    .ConfigureAwait(false);

                if (body.Distribute && result.ClusterPublish is { AllSucceeded: false } publish)
                    return ClusterPublishIncomplete(body.Domain, publish, result);

                return Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/domains/{domain}/version", async (
            string domain,
            AdminVersionRequest? body,
            ICacheOrchestratorManagement management,
            CancellationToken cancellationToken) =>
        {
            try
            {
                AdminDomainMutationResultDto result = await management
                    .SetVersionAsync(domain, body, cancellationToken)
                    .ConfigureAwait(false);
                return MutationResult(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/domain-settings/catalog", (ICacheOrchestratorManagement management) =>
            Results.Ok(management.GetDomainSettingsCatalog()));

        group.MapMethods("/domains/{domain}/settings", ["PATCH"], async (
            string domain,
            AdminSettingsPatchRequest? body,
            ICacheOrchestratorManagement management,
            CancellationToken cancellationToken) =>
        {
            if (body is null)
                return Results.BadRequest(new { error = "Request body is required." });

            try
            {
                AdminDomainMutationResultDto result = await management
                    .PatchSettingsAsync(domain, body, cancellationToken)
                    .ConfigureAwait(false);
                return MutationResult(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return endpoints;
    }

    private static IResult MutationResult(AdminDomainMutationResultDto result) =>
        result.LocalInvalidationErrors.Count > 0
            ? Results.Json(new { error = "Local settings invalidation incomplete.", localApplied = true, result },
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : result.ClusterPublish is { AllSucceeded: false } publish
            ? ClusterPublishIncomplete(result.Domain, publish, payload: null)
            : Results.Ok(result);

    private static IResult ClusterPublishIncomplete(
        string? domain,
        ClusterPublishResult publish,
        object? payload)
    {
        var peerFailures = publish.Failures
            .Select(f => new { peerId = f.PeerId, error = f.Error })
            .ToArray();

        return Results.Json(
            new
            {
                error = "Cluster publish incomplete.",
                domain,
                localApplied = true,
                peerFailures,
                result = payload,
            },
            statusCode: StatusCodes.Status409Conflict);
    }
}
