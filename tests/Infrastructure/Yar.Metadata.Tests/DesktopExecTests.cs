namespace Yar.Metadata.Tests;

public class DesktopExecTests
{
    [Theory]
    [InlineData("krita %F", new[] { "krita", "%F" })]
    [InlineData("  /usr/bin/app   --flag\tvalue ", new[] { "/usr/bin/app", "--flag", "value" })]
    [InlineData("\"/opt/My App/run\" %u", new[] { "/opt/My App/run", "%u" })]
    [InlineData("env \"A=b c\" app", new[] { "env", "A=b c", "app" })]
    [InlineData("app \"say \\\"hi\\\"\" \"cost \\$5\" \"back\\\\slash\"", new[] { "app", "say \"hi\"", "cost $5", "back\\slash" })]
    [InlineData("app \"\"", new[] { "app", "" })]
    [InlineData("app; rm -rf ~", new[] { "app;", "rm", "-rf", "~" })]
    public void Splits_arguments(string exec, string[] expected)
    {
        Assert.Equal(expected, DesktopExec.Split(exec));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"unterminated")]
    public void Empty_or_broken_values_return_null(string? exec)
    {
        Assert.Null(DesktopExec.Split(exec));
    }

    [Fact]
    public void Resolves_absolute_and_bare_programs()
    {
        var directory = Directory.CreateTempSubdirectory("yar-exec-").FullName;
        try
        {
            var program = Path.Combine(directory, "my tool");
            File.WriteAllText(program, "");

            Assert.Equal(program, DesktopExec.ResolveProgram($"\"{program}\" %f", []));
            Assert.Equal(program, DesktopExec.ResolveProgram("\"my tool\" --x", ["relative/ignored", directory]));
            Assert.Null(DesktopExec.ResolveProgram("missing-tool", [directory]));
            Assert.Null(DesktopExec.ResolveProgram("/nonexistent/tool", [directory]));
            Assert.Null(DesktopExec.ResolveProgram("sub/tool", [directory]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
