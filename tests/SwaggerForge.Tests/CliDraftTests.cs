using BrokerForge.Cli;

namespace SwaggerForge.Tests;

public class CliDraftTests
{
    private static string WidgetAnswers(DirectoryInfo root)
    {
        var file = Path.Combine(root.FullName, "widgets-answers.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(DrafterTests.AnswersFor("unsupported.yaml")));
        return file;
    }

    [Fact]
    public void Cli_Draft_WritesSchemaDescriptorsAndReport()
    {
        var root = Directory.CreateTempSubdirectory("bf-draft-");
        try
        {
            var output = new StringWriter();
            var code = DraftCommand.Run(new[] { DrafterTests.Fixture("unsupported.yaml"), "--instance", "WidgetsAPI", "--prefix", "ext", "--table", "ext_widget", "--out", "out", "--root", root.FullName, "--answers", WidgetAnswers(root) }, output);
            Assert.True(code == 0, output.ToString());
            Assert.True(File.Exists(Path.Combine(root.FullName, "schemas", "WidgetsAPI.yaml")));
            Assert.True(File.Exists(Path.Combine(root.FullName, "descriptors", "WidgetsAPI", "listWidgets.yaml")));
            var report = File.ReadAllText(Path.Combine(root.FullName, "out", "WidgetsAPI-draft-report.md"));
            Assert.Contains("/widgets/{widgetId}/variant", report);
            Assert.Contains("oneOf", report);

            var validate = new StringWriter();
            Assert.Equal(0, ValidateCommand.Run(new[] { Path.Combine(root.FullName, "descriptors"), "--schemas", Path.Combine(root.FullName, "schemas") }, validate));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public void Cli_Draft_RefusesToOverwrite()
    {
        var root = Directory.CreateTempSubdirectory("bf-draft-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "schemas"));
            File.WriteAllText(Path.Combine(root.FullName, "schemas", "WidgetsAPI.yaml"), "keep me");
            var output = new StringWriter();
            var code = DraftCommand.Run(new[] { DrafterTests.Fixture("unsupported.yaml"), "--instance", "WidgetsAPI", "--prefix", "ext", "--table", "ext_widget", "--out", "out", "--root", root.FullName, "--answers", WidgetAnswers(root) }, output);
            Assert.Equal(1, code);
            Assert.Contains("WidgetsAPI.yaml", output.ToString());
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(root.FullName, "schemas", "WidgetsAPI.yaml")));
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "descriptors")));
        }
        finally
        {
            root.Delete(true);
        }
    }
}
