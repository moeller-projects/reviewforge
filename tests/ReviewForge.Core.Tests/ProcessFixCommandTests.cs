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
    [InlineData("cmd 'quoted'")]
    [InlineData("cmd \"quoted\"")]
    [InlineData("cmd \\path")]
    [InlineData("cmd $HOME")]
    [InlineData("cmd *.cs")]
    [InlineData("cmd [abc]")]
    public void Parse_rejects_shell_metacharacters(string command)
    {
        var ex = Assert.Throws<ArgumentException>(() => ProcessFixCommand.Parse(command));
        Assert.Contains("metacharacter", ex.Message);
    }
}
