using ReviewForge.Core.Analysis;
using ReviewForge.Core.AutoFix;
using ReviewForge.Core.AutoFix.Fixers;
using ReviewForge.Core.Domain;
using Xunit;

namespace ReviewForge.Core.Tests;

public class AutoFixFixerTests
{
    private const string Path = "src/file.txt";

    private static FixContext Ctx(string ruleId, string[] lines, int startLine, int endLine = -1)
        => new(
            new RichFinding
            {
                RuleId = ruleId,
                Title = "t",
                Severity = "medium",
                Category = "bug",
                Description = "d",
                Anchor = new FindingAnchor(Path, startLine, endLine < 0 ? startLine : endLine),
            },
            Path,
            lines,
            DiffIndex.Parse(string.Empty));

    // ---- HomoglyphIdentifierFixer ----

    [Fact]
    public void Homoglyph_replaces_mixed_script_identifier()
    {
        // "stаte" contains a Cyrillic 'а'.
        var fixer = new HomoglyphIdentifierFixer("homoglyph/mixed-script-identifier");
        var proposal = fixer.TryPropose(Ctx(fixer.RuleId, ["var stаte = 1;"], 1));
        Assert.NotNull(proposal);
        Assert.Equal("var state = 1;", proposal!.Replacement);
        Assert.Equal(1, proposal.StartLine);
        Assert.Equal("homoglyph/mixed-script-identifier", fixer.RuleId);
    }

    [Fact]
    public void Homoglyph_declines_when_two_suspicious_tokens_on_line()
    {
        var fixer = new HomoglyphIdentifierFixer("homoglyph/mixed-script-identifier");
        Assert.Null(fixer.TryPropose(Ctx(fixer.RuleId, ["var stаte = pаuse;"], 1)));
    }

    [Fact]
    public void Homoglyph_declines_multi_line_anchor()
    {
        var fixer = new HomoglyphIdentifierFixer("homoglyph/confusable-keyword");
        Assert.Null(fixer.TryPropose(Ctx(fixer.RuleId, ["stаte", "x"], 1, 2)));
    }

    [Fact]
    public void Homoglyph_declines_anchor_out_of_range()
    {
        var fixer = new HomoglyphIdentifierFixer("homoglyph/mixed-script-identifier");
        Assert.Null(fixer.TryPropose(Ctx(fixer.RuleId, ["ok"], 9)));
    }

    [Fact]
    public void Homoglyph_confusable_keyword_variant_registered_per_rule()
    {
        var fixer = new HomoglyphIdentifierFixer("homoglyph/confusable-keyword");
        Assert.Equal("homoglyph/confusable-keyword", fixer.RuleId);
    }

    // ---- BashUnquotedVarsFixer ----

    private static BashUnquotedVarsFixer Bash() => new();

    [Fact]
    public void Bash_quotes_simple_variable()
    {
        var proposal = Bash().TryPropose(Ctx("bash.unquoted-vars", ["echo $name"], 1));
        Assert.Equal("echo \"$name\"", proposal!.Replacement);
    }

    [Fact]
    public void Bash_quotes_braced_variable_and_preserves_indent()
    {
        var proposal = Bash().TryPropose(Ctx("bash.unquoted-vars", ["    cp ${src} ${dst}"], 1));
        Assert.Equal("    cp \"${src}\" \"${dst}\"", proposal!.Replacement);
    }

    [Fact]
    public void Bash_leaves_already_quoted_variables()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["echo \"$name\""], 1)));

    [Fact]
    public void Bash_leaves_single_quoted_content()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["echo '$name'"], 1)));

    [Fact]
    public void Bash_leaves_special_variables()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["exit $$ $? $1 $@"], 1)));

    [Fact]
    public void Bash_honors_backslash_escape()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", [@"echo \$name"], 1)));

    [Fact]
    public void Bash_declines_when_command_substitution_contains_quotes()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["echo $(echo \"x $name\")"], 1)));

    [Fact]
    public void Bash_declines_here_doc_line()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["cat <<EOF $name"], 1)));

    [Fact]
    public void Bash_declines_unbalanced_quotes()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["echo \"$name"], 1)));

    [Fact]
    public void Bash_declines_anchor_out_of_range()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["echo hi"], 5)));

    // ---- BashSetEMissingFixer ----

    private static BashSetEMissingFixer SetE() => new();

    [Fact]
    public void SetE_appends_after_shebang()
    {
        var proposal = SetE().TryPropose(Ctx("bash.set-e-missing", ["#!/bin/bash", "echo hi"], 1));
        Assert.Equal("#!/bin/bash\nset -euo pipefail", proposal!.Replacement);
    }

    [Fact]
    public void SetE_declines_when_anchor_not_line_one()
        => Assert.Null(SetE().TryPropose(Ctx("bash.set-e-missing", ["#!/bin/bash", "echo hi"], 2)));

    [Fact]
    public void SetE_declines_without_shebang()
        => Assert.Null(SetE().TryPropose(Ctx("bash.set-e-missing", ["echo hi"], 1)));

    [Fact]
    public void SetE_declines_when_set_e_already_present()
        => Assert.Null(SetE().TryPropose(Ctx("bash.set-e-missing", ["#!/bin/sh", "set -e", "echo hi"], 1)));

    [Fact]
    public void SetE_declines_when_set_ex_already_present()
        => Assert.Null(SetE().TryPropose(Ctx("bash.set-e-missing", ["#!/bin/sh", "set -ex", "echo hi"], 1)));

    // ---- PythonMutableDefaultArgFixer ----

    private static PythonMutableDefaultArgFixer Py() => new();

    [Fact]
    public void Py_replaces_list_default_with_none_guard()
    {
        var proposal = Py().TryPropose(Ctx("py.mutable-default-arg", ["def f(items=[]):", "    return items"], 1));
        Assert.Equal("def f(items= None):\n    if items is None: items = []", proposal!.Replacement);
    }

    [Fact]
    public void Py_replaces_dict_default_and_uses_body_indent()
    {
        var lines = new[] { "def f(config={}):", "", "        return config" };
        var proposal = Py().TryPropose(Ctx("py.mutable-default-arg", lines, 1));
        Assert.Equal("def f(config= None):\n        if config is None: config = {}", proposal!.Replacement);
    }

    [Fact]
    public void Py_declines_multi_line_signature()
    {
        var lines = new[] { "def f(", "    items=[]):", "    return items" };
        Assert.Null(Py().TryPropose(Ctx("py.mutable-default-arg", lines, 1)));
    }

    [Fact]
    public void Py_declines_without_body_indent()
        => Assert.Null(Py().TryPropose(Ctx("py.mutable-default-arg", ["def f(items=[]):"], 1)));

    [Fact]
    public void Py_declines_callable_mutable_default()
        => Assert.Null(Py().TryPropose(Ctx("py.mutable-default-arg", ["def f(items=list()):", "    return items"], 1)));

    [Fact]
    public void Py_handles_annotated_parameter()
    {
        var proposal = Py().TryPropose(Ctx("py.mutable-default-arg", ["def f(items: list = []):", "    return items"], 1));
        Assert.Equal("def f(items: list = None):\n    if items is None: items = []", proposal!.Replacement);
    }

    // ---- DockerAddToCopyFixer ----

    private static DockerAddToCopyFixer Docker() => new();

    [Fact]
    public void Docker_replaces_add_with_copy()
    {
        var proposal = Docker().TryPropose(Ctx("docker.add-vs-copy", ["ADD ./src /app"], 1));
        Assert.Equal("COPY ./src /app", proposal!.Replacement);
    }

    [Fact]
    public void Docker_preserves_indentation()
    {
        var proposal = Docker().TryPropose(Ctx("docker.add-vs-copy", ["  ADD a b"], 1));
        Assert.Equal("  COPY a b", proposal!.Replacement);
    }

    [Fact]
    public void Docker_declines_url_source()
        => Assert.Null(Docker().TryPropose(Ctx("docker.add-vs-copy", ["ADD https://example.com/app.tar.gz /app"], 1)));

    [Fact]
    public void Docker_declines_local_archive()
        => Assert.Null(Docker().TryPropose(Ctx("docker.add-vs-copy", ["ADD app.zip /app"], 1)));

    [Fact]
    public void Docker_declines_continuation_line()
        => Assert.Null(Docker().TryPropose(Ctx("docker.add-vs-copy", ["ADD app.tar /app \\"], 1)));

    [Fact]
    public void Docker_declines_non_add_line()
        => Assert.Null(Docker().TryPropose(Ctx("docker.add-vs-copy", ["RUN echo hi"], 1)));

    // ---- FindingFixerRegistry ----

    [Fact]
    public void Registry_resolves_by_rule_id_ordinal()
    {
        var fixer = Bash();
        var registry = new FindingFixerRegistry([fixer]);
        Assert.True(registry.TryGet("bash.unquoted-vars", out var resolved));
        Assert.Same(fixer, resolved);
        Assert.False(registry.TryGet("BASH.UNQUOTED-VARS", out _));
        Assert.Equal(["bash.unquoted-vars"], registry.RegisteredRuleIds);
    }

    [Fact]
    public void Registry_rejects_duplicate_rule_ids()
        => Assert.Throws<InvalidOperationException>(
            () => new FindingFixerRegistry([new BashSetEMissingFixer(), new BashSetEMissingFixer()]));

    // ---- record shape ----

    [Fact]
    public void AppliedFix_and_proposal_defaults()
    {
        var proposal = new FixProposal("f", 1, 1, "x", "r");
        Assert.Equal(FixOrigin.Deterministic, proposal.Origin);
        Assert.Null(proposal.SourceThreadId);
        var applied = new AppliedFix("k", proposal, "verifier");
        Assert.Equal("k", applied.DedupeKey);
        Assert.Equal("verifier", applied.VerifierName);
        var commanded = proposal with {Origin = FixOrigin.LlmCommanded, SourceThreadId = 7};
        Assert.Equal(7, commanded.SourceThreadId);
    }
}
