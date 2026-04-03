using System.Diagnostics;
using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace UnityRoslynUpdater;

internal sealed class PatchScriptCompilationOperation : IUpdateOperation
{
    private const string DllName = "ScriptCompilationBuildProgram.Data.dll";
    private const string Namespace = "ScriptCompilationBuildProgram.Data";

    private static readonly HashSet<string> SkipFields = ["DotnetRuntimePath", "DotnetRoslynPath"];

    public async Task ExecuteAsync(UpdateContext context)
    {
        var dllPath = Path.Combine(context.EditorDataPath, "Tools", "BuildPipeline", DllName);
        var backupPath = dllPath + ".bak";

        if (!File.Exists(dllPath))
        {
            Console.WriteLine($"{DllName} not found at {dllPath}.");
            return;
        }

        // Save original on first run; always decompile from the unpatched backup
        if (!File.Exists(backupPath))
            File.Copy(dllPath, backupPath);

        var decompiled = DecompileToSource(backupPath);

        if (decompiled is null)
        {
            Console.Error.WriteLine($"Failed to decompile {DllName}.");
            return;
        }

        // Load injection source from embedded resource
        using var resourceStream = typeof(Program).Assembly.GetManifestResourceStream("InjectedCompilerRedirect.cs")!;
        using var reader = new StreamReader(resourceStream);
        var injectionSource = await reader.ReadToEndAsync();

        // Compile using dotnet build in a temp directory
        var compiledBytes = await CompileWithDotnetBuild(decompiled, injectionSource);

        if (compiledBytes is null)
            return;

        File.WriteAllBytes(dllPath, compiledBytes);
        Console.WriteLine($"Patched {DllName}.");
    }

    private static string? DecompileToSource(string dllPath)
    {
        var assembly = AssemblyDefinition.FromFile(dllPath);

        if (assembly.ManifestModule is not { } module)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine($"namespace {Namespace};");
        sb.AppendLine();

        foreach (var type in module.TopLevelTypes)
        {
            if (type.Namespace != Namespace || type.Name == "<Module>")
                continue;

            var isPartial = type.Name == "ScriptCompilationData";
            var keyword = type.IsSealed && type.IsAbstract ? "static" : "";
            var partial = isPartial ? "partial" : "";

            sb.AppendLine($"public {keyword} {partial} class {type.Name}".Replace("  ", " ").Trim());
            sb.AppendLine("{");

            foreach (var field in type.Fields)
            {
                if (!field.IsPublic)
                    continue;

                if (isPartial && SkipFields.Contains(field.Name!))
                    continue;

                var typeName = ToCSharpTypeName(field.Signature!.FieldType);
                var isConst = field.IsStatic && field.IsLiteral;

                if (isConst && field.Constant?.Value is { } blob)
                {
                    var valueStr = typeName == "string"
                        ? $"\"{Encoding.Unicode.GetString(blob.Data).TrimEnd('\0')}\""
                        : blob.InterpretData(field.Signature!.FieldType.ElementType)?.ToString() ?? "null";
                    sb.AppendLine($"    public const {typeName} {field.Name} = {valueStr};");
                }
                else
                {
                    var initializer = GetFieldInitializer(field.Signature!.FieldType);
                    sb.AppendLine($"    public {typeName} {field.Name}{initializer};");
                }
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string ToCSharpTypeName(TypeSignature type)
    {
        if (type is SzArrayTypeSignature arrayType)
            return $"{ToCSharpTypeName(arrayType.BaseType)}[]";

        return type.FullName switch
        {
            "System.String" => "string",
            "System.Boolean" => "bool",
            "System.Int32" => "int",
            "System.Int64" => "long",
            "System.Single" => "float",
            "System.Double" => "double",
            "System.Object" => "object",
            _ => type.Name ?? type.FullName,
        };
    }

    private static string GetFieldInitializer(TypeSignature type)
    {
        if (type is SzArrayTypeSignature arrayType)
            return $" = new {ToCSharpTypeName(arrayType.BaseType)}[0]";

        return "";
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
}
