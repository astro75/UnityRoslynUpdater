using System.Diagnostics;
using System.Security.Principal;
using UnityRoslynUpdater;

// Re-launch as admin if not elevated
if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
{
    var exe = Environment.ProcessPath!;
    var processArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
    var startInfo = new ProcessStartInfo
    {
        FileName = exe,
        Arguments = string.Join(' ', processArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
        Verb = "runas",
        UseShellExecute = true,
    };

    try
    {
        var process = Process.Start(startInfo);
        if (process is not null)
            await process.WaitForExitAsync();
    }
    catch (System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine("Administrator privileges are required.");
    }

    return;
}

string editorPath = args.Length >= 1 ? Path.GetFullPath(args[0]) : EditorFinder.ChooseEditorFullPath();

if (string.IsNullOrEmpty(editorPath) || !Directory.Exists(Path.Combine(editorPath, "Data")))
{
    Console.Error.WriteLine(
        """
        Please provide the path to the 'Editor' directory of the Unity
        installation that you wish to link to a newer .NET SDK version.
        """
    );

    return;
}

var context = new UpdateContext
{
    EditorPath = editorPath
};

IUpdateOperation[] operations =
[
    // new UpdateSdkOperation(),
    new PatchSourceGeneratorOperation(),
    // new PatchUnityAssembliesOperation(),
    new PatchScriptCompilationOperation(),
    new DownloadBclDocumentationOperation(),
];

foreach (var operation in operations)
{
    await operation.ExecuteAsync(context);
}
