using UnityRoslynUpdater;

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

// Check write access to the editor directory
try
{
    var testFile = Path.Combine(editorPath, "Data", ".write-test");
    await File.WriteAllTextAsync(testFile, "");
    File.Delete(testFile);
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine($"No write access to '{editorPath}'.");
    Console.Error.WriteLine("Please run this tool as Administrator.");
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
