using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UnityRoslynUpdater;

internal sealed partial class PatchScriptCompilationOperation : IUpdateOperation
{
    private const string GitHubRawBaseUrl = "https://raw.githubusercontent.com/Unity-Technologies/UnityCsReference";
    private const string DataCsPath = "Editor/IncrementalBuildPipeline/ScriptCompilationBuildProgram.Data/Data.cs";
    private const string DllName = "ScriptCompilationBuildProgram.Data.dll";

    public async Task ExecuteAsync(UpdateContext context)
    {
        var dllPath = Path.Combine(context.EditorDataPath, "Tools", "BuildPipeline", DllName);

        if (!File.Exists(dllPath))
        {
            Console.WriteLine($"{DllName} not found at {dllPath}.");
            return;
        }

        var version = ParseUnityVersion(context.EditorPath);

        if (version is null)
        {
            Console.Error.WriteLine("Could not determine Unity version from editor path.");
            return;
        }

        // Fetch original source from UnityCsReference for this Unity version
        var sourceUrl = $"{GitHubRawBaseUrl}/{version}/{DataCsPath}";

        using var httpClient = new HttpClient();
        HttpResponseMessage response;

        try
        {
            response = await httpClient.GetAsync(sourceUrl);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to fetch source from GitHub: {ex.Message}");
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Failed to fetch {sourceUrl}: {response.StatusCode}");
            return;
        }

        var originalSource = await response.Content.ReadAsStringAsync();

        // Modify source: make ScriptCompilationData partial, remove the two fields we're replacing
        var modifiedSource = originalSource
            .Replace("public class ScriptCompilationData", "public partial class ScriptCompilationData");

        modifiedSource = DotnetRuntimePathFieldRegex().Replace(modifiedSource, "");
        modifiedSource = DotnetRoslynPathFieldRegex().Replace(modifiedSource, "");

        // Load injection source from embedded resource
        using var resourceStream = typeof(Program).Assembly.GetManifestResourceStream("InjectedCompilerRedirect.cs")!;
        using var reader = new StreamReader(resourceStream);
        var injectionSource = await reader.ReadToEndAsync();

        // Find netstandard.dll reference assembly
        var netstandardPath = Path.Combine(context.EditorDataPath, "NetStandard", "Ref", "2.1.0", "netstandard.dll");

        if (!File.Exists(netstandardPath))
        {
            Console.Error.WriteLine($"netstandard.dll not found at {netstandardPath}.");
            return;
        }

        // Compile
        var compiledBytes = Compile(modifiedSource, injectionSource, netstandardPath);

        if (compiledBytes is null)
            return;

        // Replace DLL with backup
        var backup = File.ReadAllBytes(dllPath);

        try
        {
            File.WriteAllBytes(dllPath, compiledBytes);
        }
        catch
        {
            File.WriteAllBytes(dllPath, backup);
            throw;
        }

        Console.WriteLine($"Patched {DllName} for Unity {version}.");
    }

    private static string? ParseUnityVersion(string editorPath)
    {
        // Editor path is like: C:\...\6000.0.23f1\Editor or C:\...\2022.3.46f1\Editor
        // Walk up directory components looking for a Unity version pattern
        var path = Path.GetFullPath(editorPath);

        while (path is not null)
        {
            var dirName = Path.GetFileName(path);

            if (dirName is not null && UnityVersionRegex().IsMatch(dirName))
                return dirName;

            path = Path.GetDirectoryName(path);
        }

        return null;
    }

    private static byte[]? Compile(string modifiedDataCs, string injectionCs, string netstandardPath)
    {
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(modifiedDataCs),
            CSharpSyntaxTree.ParseText(injectionCs),
        };

        var references = new MetadataReference[]
        {
            MetadataReference.CreateFromFile(netstandardPath),
        };

        var compilation = CSharpCompilation.Create(
            "ScriptCompilationBuildProgram.Data",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable)
        );

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            Console.Error.WriteLine("Compilation failed:");

            foreach (var diagnostic in result.Diagnostics)
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                    Console.Error.WriteLine($"  {diagnostic}");
            }

            return null;
        }

        return ms.ToArray();
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+[a-z]\d+$")]
    private static partial Regex UnityVersionRegex();

    [GeneratedRegex(@"^[ \t]*public\s+string\s+DotnetRuntimePath\s*;[ \t]*\r?\n", RegexOptions.Multiline)]
    private static partial Regex DotnetRuntimePathFieldRegex();

    [GeneratedRegex(@"^[ \t]*public\s+string\s+DotnetRoslynPath\s*;[ \t]*\r?\n", RegexOptions.Multiline)]
    private static partial Regex DotnetRoslynPathFieldRegex();
}
