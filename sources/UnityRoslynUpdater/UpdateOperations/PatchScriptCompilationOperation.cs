using System.Diagnostics;
using System.Text.RegularExpressions;

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

        // Compile using dotnet build in a temp directory
        var compiledBytes = await CompileWithDotnetBuild(modifiedSource, injectionSource);

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

    private static async Task<byte[]?> CompileWithDotnetBuild(string modifiedDataCs, string injectionCs)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"UnityRoslynUpdater_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write source files
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Data.cs"), modifiedDataCs);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "InjectedCompilerRedirect.cs"), injectionCs);

            // Write .csproj
            await File.WriteAllTextAsync(Path.Combine(tempDir, "ScriptCompilationBuildProgram.Data.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.1</TargetFramework>
                    <LangVersion>latest</LangVersion>
                    <Nullable>enable</Nullable>
                    <EnableDefaultItems>true</EnableDefaultItems>
                  </PropertyGroup>
                </Project>
                """);

            // Run dotnet build
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "build -c Release --nologo",
                    WorkingDirectory = tempDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine("dotnet build failed:");
                Console.Error.WriteLine(stdout);
                Console.Error.WriteLine(stderr);
                return null;
            }

            // Read the compiled DLL
            var outputPath = Path.Combine(tempDir, "bin", "Release", "netstandard2.1", DllName);
            return await File.ReadAllBytesAsync(outputPath);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+[a-z]\d+$")]
    private static partial Regex UnityVersionRegex();

    [GeneratedRegex(@"^[ \t]*public\s+string\s+DotnetRuntimePath\s*;[ \t]*\r?\n", RegexOptions.Multiline)]
    private static partial Regex DotnetRuntimePathFieldRegex();

    [GeneratedRegex(@"^[ \t]*public\s+string\s+DotnetRoslynPath\s*;[ \t]*\r?\n", RegexOptions.Multiline)]
    private static partial Regex DotnetRoslynPathFieldRegex();
}
