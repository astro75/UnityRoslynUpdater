#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace ScriptCompilationBuildProgram.Data;

public partial class ScriptCompilationData {
  // Serializer uses this value instead of `DotnetRoslynPath` field
  [DataMember(Name = nameof(DotnetRoslynPath))]
  public string DotnetRoslynPathProp => StaticState.bothPath?.compilerPath ?? DotnetRoslynPath;

  [IgnoreDataMember] public string DotnetRoslynPath = null!;


  [DataMember(Name = nameof(DotnetRuntimePath))]
  public string DotnetRuntimePathProp => StaticState.bothPath?.dotnetPath ?? DotnetRuntimePath;

  [IgnoreDataMember] public string DotnetRuntimePath = null!;
}


/// <summary>
/// This project is a decompiled copy of ScriptCompilationBuildProgram.Data.dll from Unity editor. It is used to
/// inject a custom compiler into Unity.
/// <para/>
/// Added custom compiler detection here. Detection runs when Unity serializes the data to a json file using
/// TinyJson.JSONWriter.
/// <para/>
/// Replace a couple of fields upon serialization using <see cref="IgnoreDataMemberAttribute"/> and
/// <see cref="DataMemberAttribute"/>. The data is serialized using C# and later used parsed in native code.
/// </summary>
static class StaticState {
  static bool _bothSearched;
  static (string dotnetPath, string compilerPath)? _bothPath;

  public static (string dotnetPath, string compilerPath)? bothPath {
    get {
      if (!_bothSearched) {
        _bothSearched = true;
        _bothPath = findBothPath();
      }
      return _bothPath;
    }
  }

  static (string dotnetPath, string compilerPath)? findBothPath() {
    var compilerPath = findCompilerPath();
    if (compilerPath is null) {
      return null;
    }

    var dotnetPath = Path.GetDirectoryName(findInPath(
      getPathEntries().Concat(perPlatform(
        onWindows: () => new[] { @"C:\Program Files\dotnet\" },
        onPosix: () => new[] { @"/usr/local/bin/", @"/usr/local/share/dotnet" }
      )),
      dotNetExecutableName()
    )) ?? throw new Exception(
      $"Can't determine a path for .NET Core executable '{dotNetExecutableName()}'. Please ensure it is in your $PATH."
    );

    return (dotnetPath, compilerPath);
  }

  static string? findCompilerPath() {
    var projectPath = Directory.GetCurrentDirectory();
    {
      // New
      var compilerDirectory = Path.Combine(projectPath, "roslyn-compiler");
      var compilerDll = Path.Combine(compilerDirectory, "csc.dll");
      if (File.Exists(compilerDll)) {
        return compilerDirectory;
      }
    }
    {
      // Old
      var compilerDirectory = Path.Combine(projectPath, "Roslyn", "net6.0");
      var compilerDll = Path.Combine(compilerDirectory, "csc.dll");
      if (File.Exists(compilerDll)) {
        return compilerDirectory;
      }
    }
    return null;
  }

  static string? findInPath(IEnumerable<string> entries, string executable) =>
    entries.Select(entry => Path.Combine(entry, executable)).FirstOrDefault(File.Exists);

  /// <summary>Gets the entries on $PATH.</summary>
  static string[] getPathEntries() =>
    Environment.GetEnvironmentVariable("PATH")?.Split(perPlatform(onWindows: () => ';', onPosix: () => ':')) ?? Array.Empty<string>();

  static string dotNetExecutableName() =>
    perPlatform(onWindows: () => "dotnet.exe", onPosix: () => "dotnet");

  static R perPlatform<R>(Func<R> onWindows, Func<R> onPosix) =>
    Environment.OSVersion.Platform switch {
      PlatformID.MacOSX or PlatformID.Unix => onPosix(),
      PlatformID.Win32NT or PlatformID.Win32S or PlatformID.Win32Windows or PlatformID.WinCE => onWindows(),
      var other => throw new ArgumentOutOfRangeException($"Unsupported platform: {other}")
    };
}
