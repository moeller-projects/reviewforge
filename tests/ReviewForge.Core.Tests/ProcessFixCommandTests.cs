using ReviewForge.Core.AutoFix;
using Xunit;

namespace ReviewForge.Core.Tests;

public class ProcessFixCommandTests
{
    [Theory]
    [InlineData("make verify", "make", new string[] {"verify"})]
    [InlineData("  dotnet   test   ", "dotnet", new string[] {"test"})]
    [InlineData("./verify.sh", "./verify.sh", new string[] {})]
    [InlineData("bash scripts/check.sh --fast", "bash", new string[] {"scripts/check.sh", "--fast"})]

    public void Parse_splits_executable_and_arguments(string command, string exe, string[] args)
    {
        var (executable, arguments) = ProcessFixCommand.Parse(command);
        Assert.Equal(exe, executable);
        Assert.Equal(args, arguments);
    }
    [Fact]
    public void Parse_groups_double_quoted_argument_with_spaces()
    {
        var (executable, arguments) = ProcessFixCommand.Parse("verify \"path with spaces/check.sh\" --fast");

        Assert.Equal("verify", executable);
        Assert.Equal(["path with spaces/check.sh", "--fast"], arguments);
    }

    [Fact]
    public void Parse_allows_single_quote_as_plain_argument_content()
    {
        var (_, arguments) = ProcessFixCommand.Parse("echo don't");

        Assert.Equal(["don't"], arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_rejects_empty(string command)
    {
        Assert.Throws<ArgumentException>(() => ProcessFixCommand.Parse(command));
    }

    [Theory]
    [InlineData("make verify && rm -rf /")]
    [InlineData("echo $(whoami)")]
    [InlineData("run `id`")]
    [InlineData("cmd > out.txt")]
    [InlineData("cmd | tee log")]
    [InlineData("cmd; other")]
    [InlineData("cmd \\path")]
    [InlineData("cmd $HOME")]
    [InlineData("cmd *.cs")]
    [InlineData("cmd [abc]")]
    public void Parse_rejects_shell_metacharacters(string command)
    {
        var ex = Assert.Throws<ArgumentException>(() => ProcessFixCommand.Parse(command));
        Assert.Contains("metacharacter", ex.Message);
    }
    [Theory]
    [InlineData("cmd \"quoted")]
    [InlineData("\"cmd")]
    public void Parse_rejects_unbalanced_double_quotes(string command)
    {
        var ex = Assert.Throws<ArgumentException>(() => ProcessFixCommand.Parse(command));
        Assert.Contains("unbalanced", ex.Message);
    }

    [Theory]
    [InlineData("cmd arg\tvalue")]
    [InlineData("cmd arg\nvalue")]
    [InlineData("cmd arg\rvalue")]
    public void Parse_rejects_embedded_control_characters(string command)
    {
        var ex = Assert.Throws<ArgumentException>(() => ProcessFixCommand.Parse(command));
        Assert.Contains("must not contain", ex.Message);
    }
}
