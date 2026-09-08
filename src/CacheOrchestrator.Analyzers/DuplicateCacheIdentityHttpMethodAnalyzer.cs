using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;

namespace CacheOrchestrator.Analyzers;

/// <summary>
/// Flags duplicate HTTP methods across cache identity attributes applying to the same action.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DuplicateCacheIdentityHttpMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "COIDENTITY001";

    private static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Duplicate cache identity HTTP method",
        messageFormat: "HTTP method '{0}' is bound more than once by cache identity attributes on '{1}'",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Each HTTP method may be bound at most once across CacheIdentity and ContentHashCacheIdentity " +
            "attributes that apply to the same action (method and containing type).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            INamedTypeSymbol? identity = start.Compilation.GetTypeByMetadataName("CacheOrchestrator.Identity.CacheIdentityAttribute");
            INamedTypeSymbol? contentHash = start.Compilation.GetTypeByMetadataName("CacheOrchestrator.Identity.ContentHashCacheIdentityAttribute");
            if (identity is not null || contentHash is not null)
                start.RegisterSymbolAction(symbol => AnalyzeMethod(symbol, identity, contentHash), SymbolKind.Method);
        });
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, INamedTypeSymbol? identity, INamedTypeSymbol? contentHash)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.MethodKind != MethodKind.Ordinary)
            return;

        List<(string HttpMethod, AttributeData Attribute)> bindings = CollectBindings(method, identity, contentHash);
        if (bindings.Count == 0)
            return;

        string targetName = method.ContainingType is null
            ? method.Name
            : method.ContainingType.Name + "." + method.Name;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string httpMethod, AttributeData attribute) in bindings)
        {
            if (seen.Add(httpMethod))
                continue;

            Location? location = attribute.ApplicationSyntaxReference
                ?.GetSyntax(context.CancellationToken)
                .GetLocation();

            if (location is null)
            {
                if (method.Locations.Length == 0)
                    continue;
                location = method.Locations[0];
            }

            context.ReportDiagnostic(
                Diagnostic.Create(
                    Rule,
                    location,
                    httpMethod.ToUpperInvariant(),
                    targetName));
        }
    }

    private static List<(string HttpMethod, AttributeData Attribute)> CollectBindings(IMethodSymbol method, INamedTypeSymbol? identity, INamedTypeSymbol? contentHash)
    {
        var result = new List<(string, AttributeData)>();

        // Type attributes first (base → derived), then method attributes, so a conflicting
        // method-level binding is reported on the method attribute.
        var types = new List<INamedTypeSymbol>();
        for (INamedTypeSymbol? type = method.ContainingType;
             type is not null && type.SpecialType != SpecialType.System_Object;
             type = type.BaseType)
        {
            types.Add(type);
        }

        for (int i = types.Count - 1; i >= 0; i--)
        {
            foreach (AttributeData attribute in types[i].GetAttributes())
                AppendMethods(attribute, result, identity, contentHash);
        }

        var methods = new List<IMethodSymbol>();
        for (IMethodSymbol? current = method; current is not null; current = current.OverriddenMethod)
            methods.Add(current);

        for (int i = methods.Count - 1; i >= 0; i--)
        {
            foreach (AttributeData attribute in methods[i].GetAttributes())
                AppendMethods(attribute, result, identity, contentHash);
        }

        return result;
    }

    private static void AppendMethods(
        AttributeData attribute,
        List<(string HttpMethod, AttributeData Attribute)> result,
        INamedTypeSymbol? identity, INamedTypeSymbol? contentHash)
    {
        if (attribute.AttributeClass is null
            || (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, identity)
                && !SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, contentHash)))
            return;

        if (attribute.ConstructorArguments.Length == 0)
            return;

        TypedConstant methodsArg = attribute.ConstructorArguments[0];
        if (methodsArg.Kind != TypedConstantKind.Array)
            return;

        foreach (TypedConstant element in methodsArg.Values)
        {
            if (element.Value is not string raw || string.IsNullOrWhiteSpace(raw))
                continue;

            result.Add((raw.Trim(), attribute));
        }
    }

}
