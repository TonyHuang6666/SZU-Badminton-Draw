using BadmintonDraw.Desktop.Controls;

namespace BadmintonDraw.Desktop.Tests;

internal static class HelpReaderFixture
{
    // Only tests accept Markdown. Product code renders build-generated document data.
    public static void SetDocument(this HelpMarkdownView reader, string markdown, double fontSize) =>
        reader.SetDocument(HelpCompiler.HelpCompiler.CompileMarkdown(markdown), fontSize);
}
