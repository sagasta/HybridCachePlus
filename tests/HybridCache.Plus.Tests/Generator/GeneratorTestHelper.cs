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
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: "Contracts.cs");

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

    /// <summary>
    /// Runs the generator and compiles its output against the full runtime, returning generator diagnostics
    /// plus every compiler error of the resulting compilation (i.e. whether the generated code actually builds).
    /// </summary>
    public static (ImmutableArray<Diagnostic> GeneratorDiagnostics, ImmutableArray<Diagnostic> CompilationErrors, ImmutableArray<GeneratedSourceResult> GeneratedSources) RunGeneratorAndCompile(string sourceCode)
    {
        var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(sourceCode, path: "Contracts.cs"));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new HybridCachePlusGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        var generatedSources = driver.GetRunResult().Results
            .Where(r => !r.GeneratedSources.IsDefault)
            .SelectMany(r => r.GeneratedSources)
            .ToImmutableArray();

        var compilationErrors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        return (generatorDiagnostics, compilationErrors, generatedSources);
    }

    /// <summary>
    /// Runs the generator, edits an unrelated file, runs it again and returns the tracked output steps of the second run.
    /// An incremental generator must report every output step as Cached/Unchanged.
    /// </summary>
    public static ImmutableArray<IncrementalGeneratorRunStepOutput> RunTwiceWithUnrelatedEdit(string sourceCode)
    {
        var unrelated = CSharpSyntaxTree.ParseText("namespace TestApp { public class Unrelated { } }", path: "Unrelated.cs");
        var compilation = CreateCompilation(CSharpSyntaxTree.ParseText(sourceCode, path: "Contracts.cs"), unrelated);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new HybridCachePlusGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        var edited = compilation.ReplaceSyntaxTree(
            unrelated,
            CSharpSyntaxTree.ParseText("namespace TestApp { public class Unrelated { public int X { get; set; } } }", path: "Unrelated.cs"));
        driver = driver.RunGenerators(edited);

        return driver.GetRunResult().Results
            .SelectMany(r => r.TrackedOutputSteps)
            .SelectMany(kv => kv.Value)
            .SelectMany(step => step.Outputs)
            .Select(o => new IncrementalGeneratorRunStepOutput(o.Reason))
            .ToImmutableArray();
    }

    public readonly record struct IncrementalGeneratorRunStepOutput(IncrementalStepRunReason Reason);

    private static CSharpCompilation CreateCompilation(params SyntaxTree[] syntaxTrees)
    {
        var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var references = trustedPlatformAssemblies
            .Concat(
            [
                typeof(HybridCacheKeysAttribute).Assembly.Location,
                typeof(Microsoft.Extensions.Caching.Hybrid.HybridCache).Assembly.Location,
                typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location,
                typeof(Microsoft.Extensions.DependencyInjection.ActivatorUtilities).Assembly.Location,
            ])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        return CSharpCompilation.Create(
            assemblyName: "TestsAssembly",
            syntaxTrees: syntaxTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }
}
