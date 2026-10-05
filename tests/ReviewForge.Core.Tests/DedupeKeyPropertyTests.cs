using System.Text;
using ReviewForge.Core.Analysis;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class DedupeKeyPropertyTests
{
    [Fact]
    public void Compute_is_invariant_under_rule_path_case_and_snippet_whitespace()
    {
        const string rule = "security/sql-injection";
        const string path = "src/Module/Foo.cs";
        string[] vocabulary = ["account", "value", "result", "guard", "branch", "token", "null", "fallback"];
        string[] separators = [" ", "\t", "\r\n", "  \t"];
        var random = new Random(0x5eed);

        for (var sample = 0; sample < 256; sample++)
        {
            var tokens = Enumerable.Range(0, random.Next(1, 17))
                .Select(_ => vocabulary[random.Next(vocabulary.Length)])
                .ToArray();
            var expected = DedupeKey.Compute(rule, path, string.Join(' ', tokens));
            var snippet = new StringBuilder();
            for (var token = 0; token < tokens.Length; token++)
            {
                if (token > 0)
                    snippet.Append(separators[random.Next(separators.Length)]);

                snippet.Append(random.Next(2) == 0
                    ? tokens[token].ToLowerInvariant()
                    : tokens[token].ToUpperInvariant());
            }

            var variedRule = random.Next(2) == 0 ? rule.ToUpperInvariant() : rule.ToLowerInvariant();
            var variedPath = random.Next(2) == 0
                ? @"\SRC\MODULE\FOO.CS"
                : "/src/module/foo.cs";

            Assert.Equal(expected, DedupeKey.Compute(variedRule, variedPath, snippet.ToString()));
        }
    }
}
