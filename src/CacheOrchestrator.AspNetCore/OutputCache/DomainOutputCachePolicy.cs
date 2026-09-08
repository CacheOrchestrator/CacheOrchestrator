using CacheOrchestrator.Admin;
using CacheOrchestrator.Configuration;
using CacheOrchestrator.Diagnostics;
using CacheOrchestrator.Entity;
using CacheOrchestrator.Identity;
using CacheOrchestrator.Utilities;
using CacheOrchestrator.Vary;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CacheOrchestrator.OutputCache;

/// <summary>
/// Output cache policy that resolves per-domain settings, tags entries with <c>domain:{name}</c>
/// (and optional <c>entity:{domain}:{entityKind}:{id}</c>), and applies client <c>Cache-Control</c> / optional
/// diagnostic <c>X-CacheOrchestrator</c> / ETag headers.
/// </summary>
/// <remarks>
/// Instances are created as endpoint metadata (not via DI). The logger and options are resolved from
/// <see cref="HttpContext.RequestServices"/> at request time.
/// Diagnostic headers honour <c>Cache:EmitDiagnosticsHeaders</c> (default on).
/// </remarks>
public sealed class DomainOutputCachePolicy : IOutputCachePolicy, IFilterMetadata
{
    private static readonly object LoggerItemsKey = new();
    private static readonly object ResponseContributorsAppliedKey = new();
    private static readonly object ResponseClientOverrideKey = new();
    private readonly Func<HttpContext, string> _domainProvider;

    /// <summary>
    /// Creates a policy bound to a fixed cache domain.
    /// </summary>
    /// <param name="domain">Non-empty domain name (normalized on construction).</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="domain"/> is null, empty, or whitespace.</exception>
    public DomainOutputCachePolicy(string domain)
        : this(domain, resourceRouteKey: null, entityKind: null, requireEntity: false)
    {
    }

    /// <summary>
    /// Creates a policy bound to a fixed cache domain with kind-scoped tagging (lists / collections).
    /// </summary>
    /// <param name="domain">Non-empty domain name (normalized on construction).</param>
    /// <param name="entityKind">Resource type within the domain (e.g. <c>products</c>).</param>
    public DomainOutputCachePolicy(string domain, string entityKind)
        : this(domain, resourceRouteKey: null, entityKind, requireEntity: false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKind);
        if (EntityKind is null)
            throw new ArgumentException("Entity kind must contain usable characters.", nameof(entityKind));
    }

    /// <summary>
    /// Creates a policy bound to a fixed cache domain with entity tagging from a route value.
    /// </summary>
    /// <param name="domain">Non-empty domain name (normalized on construction).</param>
    /// <param name="resourceRouteKey">Route value name for the resource id (e.g. <c>"id"</c>).</param>
    /// <param name="entityKind">Resource type within the domain (e.g. <c>products</c>).</param>
    public DomainOutputCachePolicy(string domain, string resourceRouteKey, string entityKind)
        : this(domain, resourceRouteKey, entityKind, requireEntity: true)
    {
    }

    private DomainOutputCachePolicy(string domain, string? resourceRouteKey, string? entityKind, bool requireEntity = false)
    {
        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException("Domain must not be null or empty.", nameof(domain));
        if (requireEntity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceRouteKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(entityKind);
        }

        string fixedDomain = DomainName.Normalize(domain);
        _domainProvider = _ => fixedDomain;
        FixedDomain = fixedDomain;
        ResourceRouteKey = string.IsNullOrWhiteSpace(resourceRouteKey) ? null : resourceRouteKey.Trim();
        EntityKind = NormalizeConfiguredEntityKind(entityKind, requireEntity);
    }

    /// <summary>
    /// Creates a policy that resolves the cache domain per request.
    /// </summary>
    /// <param name="domainResolver">Delegate that returns the domain for the current <see cref="HttpContext"/>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="domainResolver"/> is null.</exception>
    public DomainOutputCachePolicy(Func<HttpContext, string> domainResolver)
        : this(domainResolver, resourceRouteKey: null, entityKind: null, requireEntity: false)
    {
    }

    /// <summary>
    /// Creates a policy that resolves the cache domain per request, with kind-scoped tagging.
    /// </summary>
    /// <param name="domainResolver">Delegate that returns the domain for the current <see cref="HttpContext"/>.</param>
    /// <param name="entityKind">Resource type within the domain (e.g. <c>products</c>).</param>
    public DomainOutputCachePolicy(Func<HttpContext, string> domainResolver, string entityKind)
        : this(domainResolver, resourceRouteKey: null, entityKind, requireEntity: false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKind);
        if (EntityKind is null)
            throw new ArgumentException("Entity kind must contain usable characters.", nameof(entityKind));
    }

    /// <summary>
    /// Creates a policy that resolves the cache domain per request, with entity tagging from a route value.
    /// </summary>
    /// <param name="domainResolver">Delegate that returns the domain for the current <see cref="HttpContext"/>.</param>
    /// <param name="resourceRouteKey">Route value name for the resource id (e.g. <c>"id"</c>).</param>
    /// <param name="entityKind">Resource type within the domain (e.g. <c>products</c>).</param>
    public DomainOutputCachePolicy(Func<HttpContext, string> domainResolver, string resourceRouteKey, string entityKind)
        : this(domainResolver, resourceRouteKey, entityKind, requireEntity: true)
    {
    }

    private DomainOutputCachePolicy(
        Func<HttpContext, string> domainResolver,
        string? resourceRouteKey,
        string? entityKind,
        bool requireEntity = false)
    {
        ArgumentNullException.ThrowIfNull(domainResolver);
        if (requireEntity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(resourceRouteKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(entityKind);
        }

        _domainProvider = http => domainResolver(http) ?? string.Empty;
        FixedDomain = null;
        ResourceRouteKey = string.IsNullOrWhiteSpace(resourceRouteKey) ? null : resourceRouteKey.Trim();
        EntityKind = NormalizeConfiguredEntityKind(entityKind, requireEntity);
    }

    /// <summary>
    /// Route value name used for entity tagging, or <see langword="null"/> when not configured.
    /// </summary>
    public string? ResourceRouteKey { get; }

    /// <summary>
    /// Normalized entity kind used for entity tags, or <see langword="null"/> when not configured.
    /// </summary>
    public string? EntityKind { get; }

    /// <summary>
    /// Normalized domain when this policy was constructed with a constant domain name;
    /// <see langword="null"/> when the domain is resolved per request (func/template).
    /// Used by Admin API endpoint discovery.
    /// </summary>
    public string? FixedDomain { get; }

    /// <summary>
    /// Resolves the cache domain for the current request (fixed, func, or template-backed).
    /// Used by <see cref="DataCache.IDomainDataCache"/> when domain options are not yet on the request.
    /// </summary>
    /// <param name="http">Current HTTP context.</param>
    /// <returns>
    /// Domain name, or empty when a dynamic result is unresolved or is not declared under
    /// <c>Cache:Domains</c>.
    /// </returns>
    public string ResolveDomain(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        string resolved = _domainProvider(http) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resolved) || FixedDomain is not null)
            return resolved;

        string normalized = DomainName.Normalize(resolved);
        IOptionsMonitor<CacheOrchestratorOptions>? options =
            http.RequestServices.GetService<IOptionsMonitor<CacheOrchestratorOptions>>();
        if (options?.CurrentValue.Domains.ContainsKey(normalized) == true)
            return normalized;

        ILogger<DomainOutputCachePolicy> logger = GetLogger(http);
        if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning(
                "Dynamic cache domain '{Domain}' is not configured; caching is bypassed for this request.",
                normalized);
        }
        return string.Empty;
    }

    private static string? NormalizeConfiguredEntityKind(string? entityKind, bool requireEntity)
    {
        if (string.IsNullOrWhiteSpace(entityKind))
            return null;

        string normalized = DomainName.NormalizeEntityKind(entityKind);
        if (!string.IsNullOrEmpty(normalized))
            return normalized;

        if (requireEntity)
            throw new ArgumentException("Entity kind must contain usable characters.", nameof(entityKind));

        return null;
    }

    private static ILogger<DomainOutputCachePolicy> GetLogger(HttpContext http)
    {
        if (http.Items.TryGetValue(LoggerItemsKey, out object? existing)
            && existing is ILogger<DomainOutputCachePolicy> cached)
        {
            return cached;
        }

        ILogger<DomainOutputCachePolicy> logger =
            http.RequestServices.GetService<ILogger<DomainOutputCachePolicy>>()
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DomainOutputCachePolicy>.Instance;
        http.Items[LoggerItemsKey] = logger;
        return logger;
    }

    /// <summary>
    /// Decides whether the request is cacheable and configures vary rules, tags, and TTL for the domain.
    /// </summary>
    /// <param name="context">The output cache context for the current request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ValueTask"/> that completes when the request has been evaluated.</returns>
    public async ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        HttpContext http = context.HttpContext;

        string domain = ResolveDomain(http);
        if (string.IsNullOrWhiteSpace(domain))
        {
            // Dynamic/template domain unresolved or unconfigured: fail closed.
            CacheFactoryExecutionFeatureAccessor.GetOrCreate(http).DirectFactoryDomain = "_";
            context.EnableOutputCaching = false;
            http.Response.Headers.CacheControl = "no-store";
            CacheOrchestratorMetrics.RecordOutput("_", "unresolved");
            if (ShouldEmitDiagnosticsHeaders(http))
            {
                http.Response.Headers["X-CacheOrchestrator"] = CacheOrchestratorHeaderFormatter.Format(
                    "_",
                    ClientCacheClass.NoStore,
                    OutputCacheResult.Bypass,
                    data: null,
                    ms: null,
                    version: "-");
            }
            return;
        }

        DomainHttpCacheOptions opts = http.RequestServices
            .GetRequiredService<IRequestDomainCacheOptions>()
            .EnsureDomainOptions(http, domain);

        context.EnableOutputCaching = false;

        // Identity metadata absent ⇒ GET/HEAD + Url only (no contract / body work).
        // Present ⇒ method must have a binding or Output Cache stays disabled.
        Endpoint? endpoint = http.GetEndpoint();
        CacheIdentityEndpointMetadata? identityMeta =
            endpoint?.Metadata.GetMetadata<CacheIdentityEndpointMetadata>();
        CacheIdentityBinding? identityBinding = null;
        if (identityMeta is null)
        {
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
                return;
        }
        else
        {
            if (!identityMeta.IsResolved)
                CacheIdentityEndpointResolver.EnsureResolved(endpoint!, http.RequestServices);

            if (!identityMeta.TryGetBinding(http.Request.Method, out identityBinding))
                return;
        }

        // Always respect request Cache-Control: no-store (HTTP semantics; token match, not substring).
        if (HttpHelper.ContainsCacheDirective(http.Request.Headers.CacheControl, "no-store"))
        {
            HttpHelper.ApplyNoCache(http.Response);
            RegisterResponseHeaders(http, opts, OutputCacheResult.Bypass, forceClient: ClientCacheClass.NoStore);
            return;
        }

        // Default: no shared caching for auth traffic (safe). Opt out per domain with AuthBypassMode=Never.
        if (DomainAuthEvaluator.ShouldBypassForAuth(http, opts))
        {
            HttpHelper.ApplyNoCache(http.Response);
            RegisterResponseHeaders(http, opts, OutputCacheResult.Bypass, forceClient: ClientCacheClass.Blocked);
            return;
        }

        string? entityKind = TryResolveEntityKind(http);
        string? resourceId = TryResolveResourceId(http);
        ApplyETag(http, opts, entityKind, resourceId);

        if (!opts.OutputCacheEnabled)
        {
            RegisterResponseHeaders(http, opts, OutputCacheResult.Off);
            return;
        }

        CacheIdentityMaterial? identityMaterial = null;
        if (identityBinding is not null
            && identityBinding.Kind is not CacheIdentityKind.Url)
        {
            ILogger<DomainOutputCachePolicy> logger = GetLogger(http);
            identityMaterial = await CacheIdentityApplicator
                .BuildAsync(identityBinding, http, opts, CacheVarySurface.OutputCache, logger, cancellationToken)
                .ConfigureAwait(false);

            if (identityMaterial is null)
            {
                CacheIdentityApplicator.StoreOnFeature(http, material: null, bypass: true, logger);
                RegisterResponseHeaders(http, opts, OutputCacheResult.Bypass);
                return;
            }

            CacheIdentityApplicator.StoreOnFeature(http, identityMaterial, bypass: false, logger);
        }

        context.EnableOutputCaching = true;
        context.AllowCacheLookup = true;
        context.AllowCacheStorage = true;
        context.AllowLocking = true;
        context.ResponseExpirationTimeSpan = opts.OutputTtl;

        CacheVaryMaterializer materializer =
            http.RequestServices.GetRequiredService<CacheVaryMaterializer>();
        CacheVaryMaterial vary = materializer.Build(http, opts, CacheVarySurface.OutputCache);

        context.CacheVaryByRules.VaryByHost = opts.OutputCacheVaryByHost;
        context.CacheVaryByRules.QueryKeys = CacheVaryMaterializer.CollectQueryKeysForOutputCache(http.Request.Query, opts);
        context.CacheVaryByRules.CacheKeyPrefix = opts.OutputCacheNamespace;
        context.CacheVaryByRules.VaryByValues["cache-domain"] = opts.Domain;
        context.CacheVaryByRules.VaryByValues["data-version"] = opts.VersionHex;
        context.CacheVaryByRules.VaryByValues["cache-policy"] = opts.OutputCachePolicyGeneration;

        foreach ((string key, string value) in vary.Values)
            context.CacheVaryByRules.VaryByValues[key] = value;

        if (identityMaterial is not null)
            CacheIdentityApplicator.ApplyToOutputCache(context, identityMaterial);

        if (vary.HeaderNames.Count > 0)
        {
            StringValues currentHeaders = context.CacheVaryByRules.HeaderNames;
            for (int i = 0; i < vary.HeaderNames.Count; i++)
            {
                string headerName = vary.HeaderNames[i];
                if (!currentHeaders.Contains(headerName))
                    currentHeaders = StringValues.Concat(currentHeaders, headerName);
            }

            context.CacheVaryByRules.HeaderNames = currentHeaders;
        }

        if (opts.EmitResponseVary && vary.ResponseVaryHeaderNames.Count > 0)
        {
            // Defer writing response Vary until the response starts so we do not advertise secrets.
            http.Response.OnStarting(static state =>
            {
                (HttpContext ctx, IReadOnlyList<string> names) = ((HttpContext, IReadOnlyList<string>))state!;
                AppendResponseVary(ctx.Response, names);
                return Task.CompletedTask;
            }, (http, vary.ResponseVaryHeaderNames));
        }

        context.Tags.Add(CacheTags.Domain(opts.Domain));
        if (!string.IsNullOrEmpty(entityKind) && !string.IsNullOrEmpty(resourceId))
        {
            context.Tags.Add(CacheTags.Entity(opts.Domain, entityKind, resourceId));
            context.Tags.Add(CacheTags.EntityKind(opts.Domain, entityKind));
        }
        else if (!string.IsNullOrEmpty(entityKind))
        {
            // Kind-scoped list / collection endpoints: no single resource id yet.
            context.Tags.Add(CacheTags.EntityKind(opts.Domain, entityKind));
        }

        if (http.RequestServices.GetService<ICacheResponseContributor>() is not null)
            CacheResponseTagStaging.Initialize(http);
        RegisterResponseHeaders(http, opts, OutputCacheResult.Miss);
    }

    private static void AppendResponseVary(HttpResponse response, IReadOnlyList<string> headerNames)
    {
        StringValues existing = response.Headers.Vary;
        for (int i = 0; i < headerNames.Count; i++)
        {
            string name = headerNames[i];
            if (existing.Count > 0 && existing.Contains(name))
                continue;
            existing = existing.Count == 0 ? name : StringValues.Concat(existing, name);
        }

        response.Headers.Vary = existing;
    }

    private string? TryResolveEntityKind(HttpContext http)
    {
        ICacheOrchestratorFeature? feature = http.Features.Get<ICacheOrchestratorFeature>();
        if (feature?.EntityKind is { Length: > 0 } itemKind)
        {
            return itemKind;
        }

        if (EntityKind is null)
            return null;

        feature = CacheOrchestratorFeatureAccessor.GetOrCreate(http);
        feature.EntityKind = EntityKind;
        return EntityKind;
    }

    private string? TryResolveResourceId(HttpContext http)
    {
        ICacheOrchestratorFeature? feature = http.Features.Get<ICacheOrchestratorFeature>();
        if (feature?.ResourceId is { Length: > 0 } itemId)
        {
            return itemId;
        }

        if (ResourceRouteKey is null)
            return null;

        if (!http.Request.RouteValues.TryGetValue(ResourceRouteKey, out object? raw) || raw is null)
            return null;

        string normalized = DomainName.NormalizeResourceId(raw.ToString());
        if (string.IsNullOrEmpty(normalized))
            return null;

        feature = CacheOrchestratorFeatureAccessor.GetOrCreate(http);
        feature.ResourceId = normalized;
        return normalized;
    }

    private static void ApplyETag(HttpContext http, DomainHttpCacheOptions opts, string? entityKind, string? resourceId)
    {
        switch (opts.ETagMode)
        {
            case ETagMode.None:
                http.Response.Headers.Remove(HeaderNames.ETag);
                break;

            case ETagMode.Resource:
                http.Response.Headers.ETag = resourceId is not null && entityKind is not null
                    ? CacheETagFactory.FromVersionAndResource(
                        opts.VersionHex, entityKind.AsSpan(), ":".AsSpan(), resourceId.AsSpan())
                    : resourceId is not null
                        ? CacheETagFactory.FromVersionAndResource(
                        opts.VersionHex, resourceId.AsSpan())
                        : CacheETagFactory.FromVersionAndResource(
                        opts.VersionHex,
                        (http.Request.Path.Value ?? "/").AsSpan(),
                        (http.Request.QueryString.Value ?? string.Empty).AsSpan());
                break;

            case ETagMode.Version:
            default:
                http.Response.Headers.ETag = opts.ETag;
                break;
        }
    }

    /// <summary>
    /// Called when a response is served from the output cache (HIT). Records disposition and activity tags.
    /// </summary>
    /// <param name="context">The output cache context for the current request.</param>
    /// <param name="cancellationToken">Cancellation token (unused; interface contract).</param>
    /// <returns>A completed <see cref="ValueTask"/>.</returns>
    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        HttpContext http = context.HttpContext;
        ILogger<DomainOutputCachePolicy> logger = GetLogger(http);
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("OutputCache HIT: [{Method}] {Path}", http.Request.Method, http.Request.Path);

        ICacheOrchestratorFeature feature = CacheOrchestratorFeatureAccessor.GetOrCreate(http);

        feature.Disposition = new CacheDisposition
        {
            Output = OutputCacheResult.Hit
        };

        using Activity? activity = CacheOrchestratorActivitySource.Source.StartActivity("cache.oc.hit");
        if (activity is not null)
        {
            if (feature.DomainOptions is { } opts)
            {
                activity.SetTag("domain", opts.Domain);
            }

            activity.SetTag("cache.result", "hit");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called before storing a generated response. Disables storage for non-cacheable status codes or sensitive headers.
    /// </summary>
    /// <param name="context">The output cache context for the current request.</param>
    /// <param name="cancellationToken">Cancellation token (unused; interface contract).</param>
    /// <returns>A task that completes after response contributors have staged their metadata.</returns>
    public async ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        HttpContext http = context.HttpContext;
        if (http.Features.Get<ICacheOrchestratorFeature>()?.DomainOptions is { } opts)
        {
            if (!IsCacheableStatusCode(http.Response.StatusCode, opts.CacheableStatusCodesArray)
                || http.Response.Headers.ContainsKey(HeaderNames.SetCookie)
                || http.Response.Headers.ContainsKey(HeaderNames.Authorization))
            {
                context.AllowCacheStorage = false;
            }
            else
            {
                MergePendingFootprintTags(context, opts.Domain);
            }

            if (!http.Items.ContainsKey(ResponseContributorsAppliedKey))
            {
                http.Items[ResponseContributorsAppliedKey] = true;
                bool sharedCacheEligible = IsSharedCacheEligible(http, opts);
                OutputCacheResult output = http.Features.Get<ICacheOrchestratorFeature>()?.Disposition?.Output
                    ?? OutputCacheResult.Miss;
                await ContributeResponseAsync(http, opts, sharedCacheEligible, output, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static void MergePendingFootprintTags(OutputCacheContext context, string domain)
    {
        HttpContext http = context.HttpContext;
        if (http.Features.Get<ICacheOrchestratorFeature>()?.PendingEntityFootprint is not { } footprint
            || ReferenceEquals(footprint, EntityFootprint.Empty))
        {
            return;
        }

        IReadOnlyList<string> tags = footprint.ToTags(domain);
        // Skip domain tag (already added in CacheRequestAsync); add the rest with dedupe.
        for (int i = 0; i < tags.Count; i++)
        {
            string tag = tags[i];
            if (tag.StartsWith(CacheTags.DomainPrefix, StringComparison.Ordinal))
                continue;
            context.Tags.Add(tag);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCacheableStatusCode(int statusCode, int[] allowed)
    {
        if (allowed.Length == 1)
            return allowed[0] == statusCode;

        for (int i = 0; i < allowed.Length; i++)
        {
            if (allowed[i] == statusCode)
                return true;
        }

        return false;
    }

    private static void RegisterResponseHeaders(
        HttpContext http,
        DomainHttpCacheOptions opts,
        OutputCacheResult defaultOutput,
        ClientCacheClass? forceClient = null)
    {
        if (forceClient is not null)
            http.Items[ResponseClientOverrideKey] = forceClient.Value;
        http.Response.OnStarting(ApplyHeadersAsync, (http, opts, defaultOutput, forceClient));
    }

    private static async Task ApplyHeadersAsync(object state)
    {
        (HttpContext? httpContext, DomainHttpCacheOptions? config, OutputCacheResult defOutput, ClientCacheClass? forcedClient) =
            ((HttpContext, DomainHttpCacheOptions, OutputCacheResult, ClientCacheClass?))state;

        HttpResponse response = httpContext.Response;
        int sc = response.StatusCode;
        IReadOnlyList<string>? storedResponseTags = CacheResponseTagStaging.Take(httpContext);

        ICacheOrchestratorFeature feature = CacheOrchestratorFeatureAccessor.GetOrCreate(httpContext);
        CacheDisposition disp;
        if (feature.Disposition is { } existing)
        {
            disp = existing;
            disp.Output ??= defOutput;
        }
        else
        {
            disp = new CacheDisposition { Output = defOutput };
        }

        feature.Disposition = disp;

        OutputCacheResult output = disp.Output ?? defOutput;

        string ocMetric = output switch
        {
            OutputCacheResult.Hit => "hit",
            OutputCacheResult.Bypass => "bypass",
            OutputCacheResult.Off => "off",
            OutputCacheResult.Miss => "miss",
            _ => "miss"
        };
        IAdminStatsCollector? adminStats = httpContext.RequestServices.GetService<IAdminStatsCollector>();
        bool adminOn = adminStats is { IsEnabled: true };
        CacheOrchestratorMetricsHttpExtensions.ResolveEndpointKeys(
            httpContext,
            forAdminStats: adminOn,
            forMetrics: CacheOrchestratorMetrics.IsOutputCacheEnabled,
            out string? endpointKey,
            out string? metricsRoute);
        CacheOrchestratorMetrics.RecordOutput(config.Domain, ocMetric, metricsRoute);

        if (adminOn)
        {
            adminStats!.RecordOutput(
                endpointKey,
                config.Domain,
                ocMetric);
        }

        ClientCacheClass client;
        ClientCacheSchedulePhase phase = ClientCacheSchedulePhase.NotApplicable;

        if (response.Headers.ContainsKey(HeaderNames.SetCookie) ||
            response.Headers.ContainsKey(HeaderNames.Authorization))
        {
            HttpHelper.ApplyNoCache(response);
            client = ClientCacheClass.Blocked;
        }
        else if (forcedClient is ClientCacheClass.NoStore or ClientCacheClass.Blocked)
        {
            // Auth / no-store request paths pass forceClient so client headers stay non-cacheable
            // even when the response status would otherwise allow a public/private max-age.
            HttpHelper.ApplyNoCache(response);
            client = forcedClient.Value;
        }
        else if (IsCacheableStatusCode(sc, config.CacheableStatusCodesArray) ||
                 sc == StatusCodes.Status304NotModified)
        {
            TimeProvider timeProvider = httpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            // Cookie/session identity + Public is unsafe for shared browser/CDN caches → force Private.
            // API-key-only traffic (Authorization without IsAuthenticated) may still use Public.
            ClientCacheability? cacheabilityOverride = null;
            if (config.ClientForcePrivateWhenAuthenticated
                && httpContext.User?.Identity?.IsAuthenticated == true
                && config.ClientCacheability == ClientCacheability.Public)
            {
                cacheabilityOverride = ClientCacheability.Private;
            }

            ClientCacheHeaderGenerator.Result built = ClientCacheHeaderGenerator.Build(
                config,
                timeProvider.GetUtcNow(),
                cacheabilityOverride);
            response.Headers.CacheControl = built.Header;
            phase = built.Phase;
            ClientCacheability effectiveCacheability = cacheabilityOverride ?? config.ClientCacheability;
            client = effectiveCacheability switch
            {
                ClientCacheability.Private => ClientCacheClass.Private,
                ClientCacheability.NoStore => ClientCacheClass.NoStore,
                ClientCacheability.Public => ClientCacheClass.Public,
                _ => ClientCacheClass.Public
            };
        }
        else
        {
            HttpHelper.ApplyNoCache(response);
            client = ClientCacheClass.NoStore;
        }

        string phaseWire = CacheOrchestratorHeaderFormatter.PhaseToString(phase);
        CacheOrchestratorMetrics.RecordClientSchedule(config.Domain, phaseWire);

        // Metrics always; client-visible diagnostic headers are optional (Cache:EmitDiagnosticsHeaders).
        if (ShouldEmitDiagnosticsHeaders(httpContext))
        {
            response.Headers["X-CacheOrchestrator"] = CacheOrchestratorHeaderFormatter.Format(
                config.Domain,
                client,
                output,
                data: output == OutputCacheResult.Hit ? null : disp.Data,
                ms: output == OutputCacheResult.Hit ? null : disp.ElapsedMs,
                version: config.Version,
                phase: phase);
        }

        if (!httpContext.Items.ContainsKey(ResponseContributorsAppliedKey))
        {
            httpContext.Items[ResponseContributorsAppliedKey] = true;
            await ContributeResponseAsync(
                httpContext,
                config,
                IsSharedCacheEligible(httpContext, config),
                output,
                httpContext.RequestAborted,
                storedResponseTags).ConfigureAwait(false);
        }
    }

    private static bool IsSharedCacheEligible(HttpContext http, DomainHttpCacheOptions options)
    {
        HttpResponse response = http.Response;
        if ((!IsCacheableStatusCode(response.StatusCode, options.CacheableStatusCodesArray)
                && response.StatusCode != StatusCodes.Status304NotModified)
            || response.Headers.ContainsKey(HeaderNames.SetCookie)
            || response.Headers.ContainsKey(HeaderNames.Authorization)
            || (http.Items.TryGetValue(ResponseClientOverrideKey, out object? forced)
                && forced is ClientCacheClass.NoStore or ClientCacheClass.Blocked))
        {
            return false;
        }

        return options.ClientCacheability == ClientCacheability.Public
            && (!options.ClientForcePrivateWhenAuthenticated
                || http.User?.Identity?.IsAuthenticated != true);
    }

    private static async ValueTask ContributeResponseAsync(
        HttpContext httpContext,
        DomainHttpCacheOptions config,
        bool sharedCacheEligible,
        OutputCacheResult output,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? tags = null)
    {
        ICacheOrchestratorFeature feature = CacheOrchestratorFeatureAccessor.GetOrCreate(httpContext);
        IReadOnlyList<string> responseTags = tags ?? CacheResponseTagStaging.Collect(feature, config.Domain);
        var contributionContext = new CacheResponseContext(
            httpContext,
            config,
            sharedCacheEligible,
            responseTags,
            output);

        foreach (ICacheResponseContributor contributor in
                 httpContext.RequestServices.GetServices<ICacheResponseContributor>())
        {
            try
            {
                await contributor.ContributeAsync(contributionContext, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                GetLogger(httpContext).LogWarning(
                    ex,
                    "Cache response contributor failed ({Contributor}) for domain '{Domain}'",
                    contributor.GetType().Name,
                    config.Domain);
            }
        }
    }

    /// <summary>
    /// Reads <c>Cache:EmitDiagnosticsHeaders</c> from ASP.NET Core options when available.
    /// Defaults to <see langword="true"/> if options are not registered (unit tests / edge hosts).
    /// </summary>
    private static bool ShouldEmitDiagnosticsHeaders(HttpContext httpContext)
    {
        IOptionsMonitor<CacheOrchestratorHttpOptions>? monitor =
            httpContext.RequestServices.GetService<IOptionsMonitor<CacheOrchestratorHttpOptions>>();
        return monitor?.CurrentValue.EmitDiagnosticsHeaders ?? true;
    }

}
