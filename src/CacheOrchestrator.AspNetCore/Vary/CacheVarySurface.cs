namespace CacheOrchestrator.Vary;

/// <summary>
/// Which cache surface is consuming vary material (Output Cache vs HTTP Data Cache keys).
/// </summary>
public enum CacheVarySurface
{
    /// <summary>ASP.NET Core Output Cache vary rules.</summary>
    OutputCache = 0,

    /// <summary>HTTP Data Cache key generation, independent of the selected engine.</summary>
    DataCache = 1,
}
