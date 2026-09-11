using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using HybridCache.Plus.Generators;

namespace HybridCache.Plus.Tests.Generator;

public static class GeneratorTestHelper
{
    public static (ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<GeneratedSourceResult> GeneratedSources) RunGenerator(string sourceCode)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ValueTask<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(HybridCacheKeysAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Caching.Hybrid.HybridCache).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Collections").Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Threading").Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Threading.Tasks").Location),
        };

        // Add netstandard / runtime references if available
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrEmpty(trustedPlatformAssemblies))
        {
            var platformAssemblies = trustedPlatformAssemblies!.Split(Path.PathSeparator);
            foreach (var assembly in platformAssemblies)
            {
                if (assembly.Contains("System.Private.CoreLib") ||
                    assembly.Contains("System.Runtime") ||
                    assembly.Contains("netstandard"))
                {
                    references.Add(MetadataReference.CreateFromFile(assembly));
                }
            }
        }

        var compilation = CSharpCompilation.Create(
            assemblyName: "TestsAssembly",
            syntaxTrees: [syntaxTree],
            references: references.Distinct(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new HybridCachePlusGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var runResult = driver.GetRunResult();

        var generatedSources = runResult.Results
            .Where(r => !r.GeneratedSources.IsDefault)
            .SelectMany(r => r.GeneratedSources)
            .ToImmutableArray();

        // Also merge any generator diagnostics from runResult
        var allDiagnostics = diagnostics.AddRange(runResult.Diagnostics);

        return (allDiagnostics, generatedSources);
    }
}
