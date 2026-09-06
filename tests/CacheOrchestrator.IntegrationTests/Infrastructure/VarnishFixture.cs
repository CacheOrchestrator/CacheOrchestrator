using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace CacheOrchestrator.IntegrationTests.Infrastructure;

/// <summary>Real nginx origin and Varnish xkey containers used by edge integration tests.</summary>
public sealed class VarnishFixture : IAsyncLifetime
{
    public const string VarnishImage = "varnish:9.0.3-5";
    public const string OriginImage = "nginx:1.27-alpine";

    private readonly INetwork _network;
    private readonly IContainer _origin;
    private readonly IContainer _varnish;

    public VarnishFixture()
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "Edge", "Varnish");
        _network = new NetworkBuilder().WithName($"cache-orchestrator-varnish-{Guid.NewGuid():N}").Build();
        _origin = new ContainerBuilder(OriginImage)
            .WithNetwork(_network)
            .WithNetworkAliases("edge-origin")
            .WithBindMount(Path.Combine(assets, "nginx.conf"), "/etc/nginx/conf.d/default.conf", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(80))
            .WithCleanUp(true)
            .Build();
        _varnish = new ContainerBuilder(VarnishImage)
            .WithNetwork(_network)
            .WithPortBinding(80, true)
            .WithBindMount(Path.Combine(assets, "default.vcl"), "/etc/varnish/default.vcl", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(80)
                .ForPath("/health")))
            .WithCleanUp(true)
            .Build();
    }

    public Uri Address => new($"http://{_varnish.Hostname}:{_varnish.GetMappedPublicPort(80)}");

    public async ValueTask InitializeAsync()
    {
        try
        {
            await _network.CreateAsync().ConfigureAwait(false);
            await _origin.StartAsync().ConfigureAwait(false);
            await _varnish.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                "Failed to start the Varnish integration topology. Docker must be available and able to pull the " +
                $"'{OriginImage}' and '{VarnishImage}' images.",
                ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _varnish.DisposeAsync().ConfigureAwait(false);
        await _origin.DisposeAsync().ConfigureAwait(false);
        await _network.DisposeAsync().ConfigureAwait(false);
    }
}

[CollectionDefinition("Varnish")]
public sealed class VarnishCollection : ICollectionFixture<VarnishFixture>;

/// <summary>Dedicated Varnish proxy using Testcontainers forwarding to an ASP.NET Core test origin.</summary>
internal sealed class KestrelVarnishProxy : IAsyncDisposable
{
    private readonly IContainer _container;
    private readonly string _directory;

    private KestrelVarnishProxy(IContainer container, string directory)
    {
        _container = container;
        _directory = directory;
    }

    public Uri Address => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(80)}");

    public static async Task<KestrelVarnishProxy> StartAsync(
        int originPort,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"cache-orchestrator-varnish-kestrel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string vclPath = Path.Combine(directory, "default.vcl");
        await File.WriteAllTextAsync(vclPath, CreateVcl(originPort), cancellationToken).ConfigureAwait(false);
        await TestcontainersSettings.ExposeHostPortsAsync(
            checked((ushort)originPort),
            cancellationToken).ConfigureAwait(false);

        IContainer container = new ContainerBuilder(VarnishFixture.VarnishImage)
            .WithPortBinding(80, true)
            .WithBindMount(vclPath, "/etc/varnish/default.vcl", AccessMode.ReadOnly)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(80)
                .ForPath("/health")))
            .WithCleanUp(true)
            .Build();

        try
        {
            await container.StartAsync(cancellationToken).ConfigureAwait(false);
            return new KestrelVarnishProxy(container, directory);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static string CreateVcl(int originPort) => $$"""
        vcl 4.1;

        import std;
        import xkey;

        backend default {
            .host = "host.testcontainers.internal";
            .port = "{{originPort}}";
        }

        sub vcl_recv {
            if (req.url == "/health") {
                return (synth(200, "ready"));
            }
            if (req.method == "PURGE" && req.url == "/cache-orchestrator/purge") {
                if (req.http.X-CacheOrchestrator-Key != "integration-secret") {
                    return (synth(403, "Forbidden"));
                }
                if (!req.http.xkey-purge) {
                    return (synth(400, "Missing xkey-purge"));
                }
                set req.http.n-gone = xkey.purge(req.http.xkey-purge);
                return (synth(200, "Invalidated"));
            }
            if (req.method != "GET" && req.method != "HEAD") {
                return (pass);
            }
        }

        sub vcl_backend_response {
            if (beresp.http.X-CacheOrchestrator-Edge-Cacheable == "0") {
                set beresp.uncacheable = true;
                set beresp.ttl = 0s;
            } else if (beresp.http.X-CacheOrchestrator-Edge-Cacheable == "1") {
                set beresp.http.X-Test-Origin-Edge-Ttl = beresp.http.X-CacheOrchestrator-Edge-Ttl;
                set beresp.ttl = std.duration(
                    beresp.http.X-CacheOrchestrator-Edge-Ttl + "s", 0s);
                set beresp.grace = std.duration(
                    beresp.http.X-CacheOrchestrator-Edge-Grace + "s", 0s);
            }
            unset beresp.http.X-CacheOrchestrator-Edge-Cacheable;
            unset beresp.http.X-CacheOrchestrator-Edge-Ttl;
            unset beresp.http.X-CacheOrchestrator-Edge-Grace;
        }

        sub vcl_deliver {
            if (obj.hits > 0 && obj.ttl < 0s) {
                set resp.http.Cache-Status = "Varnish; hit; detail=stale";
            } else if (obj.hits > 0) {
                set resp.http.Cache-Status = "Varnish; hit";
            } else {
                set resp.http.Cache-Status = "Varnish; fwd=uri-miss";
            }
            unset resp.http.xkey;
        }
        """;
}
