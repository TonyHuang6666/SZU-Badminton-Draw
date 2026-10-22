using System.Text;
using BadmintonDraw.Desktop.Help;
using BadmintonDraw.HelpCompiler;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: BadmintonDraw.HelpCompiler <repository-root> <manifest.json> <output.json>");
    return 2;
}
try
{
    var library = HelpCompiler.CompileLibrary(args[0], args[1]);
    var json = HelpLibraryCodec.Serialize(library);
    var output = Path.GetFullPath(args[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    // Preserve timestamps when unchanged, but always read/validate inputs on each build.
    if (!File.Exists(output) || File.ReadAllText(output) != json)
    {
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, output, overwrite: true);
    }
    Console.WriteLine($"Compiled {library.Documents.Count} help documents.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Help compilation failed: {error.Message}");
    return 1;
}
