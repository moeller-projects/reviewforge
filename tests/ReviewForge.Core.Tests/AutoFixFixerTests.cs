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
    public void Homoglyph_declines_when_token_occurs_on_another_line()
    {
        var fixer = new HomoglyphIdentifierFixer("homoglyph/mixed-script-identifier");
        Assert.Null(fixer.TryPropose(Ctx(fixer.RuleId, ["var stаte = 1;", "return stаte;"], 1)));
    }

    [Theory]
    [InlineData("var x = \"stаte\";")]
    [InlineData("var x = 'stаte';")]
    [InlineData("var x = \"prefix\\\" stаte\";")]
    public void Homoglyph_declines_token_inside_quotes(string line)
    {
        var fixer = new HomoglyphIdentifierFixer("homoglyph/mixed-script-identifier");
        Assert.Null(fixer.TryPropose(Ctx(fixer.RuleId, [line], 1)));
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
    public void Bash_declines_for_loop_word_list_expansion()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["for item in $items; do echo $item; done"], 1)));

    [Theory]
    [InlineData("[[ $left == $right ]]")]
    [InlineData("[[ $value =~ $pattern ]]")]
    [InlineData("[[ \"$left\" == $right ]]")]
    [InlineData("[[ '$left' == $right ]]")]
    public void Bash_declines_conditional_operand_expansion(string line)
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", [line], 1)));

    [Fact]
    public void Bash_quotes_expansion_in_non_comparison_conditional()
    {
        var proposal = Bash().TryPropose(Ctx("bash.unquoted-vars", ["[[ $value ]]"], 1));
        Assert.Equal("[[ \"$value\" ]]", proposal!.Replacement);
    }

    [Fact]
    public void Bash_declines_logical_line_continuation()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["echo $name \\"], 1)));

    [Fact]
    public void Bash_declines_command_substitution_even_with_nested_quotes()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["echo $(printf '%s' \"$value\") $name"], 1)));

    [Fact]
    public void Bash_declines_backtick_command_substitution()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["echo `printf '%s' $value`"], 1)));

    [Fact]
    public void Bash_declines_for_loop_without_inline_do()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["for item in $items"], 1)));

    [Fact]
    public void Bash_declines_unclosed_conditional()
        => Assert.Null(Bash().TryPropose(Ctx("bash.unquoted-vars", ["[[ $left == $right"], 1)));

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


    [Theory]
    [InlineData("#!/bin/sh")]
    [InlineData("#!/usr/bin/python3")]
    [InlineData("#!/usr/bin/env node")]
    [InlineData("#!/usr/bin/env")]
    [InlineData("#!")]
    public void SetE_declines_non_bash_interpreters(string shebang)
        => Assert.Null(SetE().TryPropose(Ctx("bash.set-e-missing", [shebang, "echo hi"], 1)));
    [Fact]
    public void SetE_accepts_env_bash_shebang()
    {
        var proposal = SetE().TryPropose(Ctx("bash.set-e-missing", ["#!/usr/bin/env bash", "echo hi"], 1));
        Assert.Equal("#!/usr/bin/env bash\nset -euo pipefail", proposal!.Replacement);
    }

    [Fact]
    public void SetE_accepts_env_option_before_bash()
    {
        var proposal = SetE().TryPropose(Ctx("bash.set-e-missing", ["#!/usr/bin/env -S bash", "echo hi"], 1));
        Assert.Equal("#!/usr/bin/env -S bash\nset -euo pipefail", proposal!.Replacement);
    }
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
        Assert.Equal("def f(items=None):\n    if items is None: items = []", proposal!.Replacement);
    }

    [Fact]
    public void Py_replaces_dict_default_and_uses_body_indent()
    {
        var lines = new[] { "def f(config={}):", "", "        return config" };
        var proposal = Py().TryPropose(Ctx("py.mutable-default-arg", lines, 1));
        Assert.Equal("def f(config=None):\n        if config is None: config = {}", proposal!.Replacement);
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

    [Fact]
    public void Py_declines_when_mutable_text_occurs_in_string_literal()
        => Assert.Null(Py().TryPropose(Ctx(
            "py.mutable-default-arg",
            ["def f(items=[], note=\"items=[]\"):", "    return items"],
            1)));

    [Fact]
    public void Py_preserves_docstring_before_guard()
    {
        var lines = new[] { "def f(items=[]):", "    \"\"\"Keep this docstring.\"\"\"", "    return items" };
        var proposal = Py().TryPropose(Ctx("py.mutable-default-arg", lines, 1));
        Assert.NotNull(proposal);
        Assert.Equal(2, proposal!.EndLine);
        Assert.Equal(
            "def f(items=None):\n    \"\"\"Keep this docstring.\"\"\"\n    if items is None: items = []",
            proposal.Replacement);
    }

    [Fact]
    public void Py_declines_multiline_docstring_before_guard()
    {
        var lines = new[] { "def f(items=[]):", "    \"\"\"Start docstring", "    end\"\"\"", "    return items" };
        Assert.Null(Py().TryPropose(Ctx("py.mutable-default-arg", lines, 1)));
    }

    [Fact]
    public void Py_preserves_single_quoted_docstring_before_guard()
    {
        var lines = new[] { "def f(items=[]):", "    '''Keep this docstring.'''", "    return items" };
        var proposal = Py().TryPropose(Ctx("py.mutable-default-arg", lines, 1));
        Assert.Equal(2, proposal!.EndLine);
        Assert.Contains("'''Keep this docstring.'''", proposal.Replacement);
    }

    [Fact]
    public void Py_skips_comment_lines_when_finding_body_indent()
    {
        var lines = new[] { "def f(items=[]):", "# comment", "    return items" };
        var proposal = Py().TryPropose(Ctx("py.mutable-default-arg", lines, 1));
        Assert.Equal("def f(items=None):\n    if items is None: items = []", proposal!.Replacement);
    }

    [Fact]
    public void Py_declines_body_at_definition_indent()
    {
        var lines = new[] { "def f(items=[]):", "return items" };
        Assert.Null(Py().TryPropose(Ctx("py.mutable-default-arg", lines, 1)));
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

    [Theory]
    [InlineData("ADD app.tar.xz /app")]
    [InlineData("ADD \"app.tar.xz\" /app")]
    [InlineData("ADD [\"app.tar.xz\", \"/app\"]")]
    public void Docker_declines_tar_xz_archives(string line)
        => Assert.Null(Docker().TryPropose(Ctx("docker.add-vs-copy", [line], 1)));

    [Fact]
    public void Docker_declines_continuation_line()
        => Assert.Null(Docker().TryPropose(Ctx("docker.add-vs-copy", ["ADD app.tar /app \\"], 1)));

    [Fact]
    public void Docker_declines_non_add_line()
        => Assert.Null(Docker().TryPropose(Ctx("docker.add-vs-copy", ["RUN echo hi"], 1)));
    [Fact]
    public void Registry_resolves_by_rule_id_case_insensitively()
    {
        var fixer = Bash();
        var registry = new FindingFixerRegistry([fixer]);
        Assert.True(registry.TryGet("bash.unquoted-vars", out var resolved));
        Assert.Same(fixer, resolved);
        Assert.True(registry.TryGet("BASH.UNQUOTED-VARS", out resolved));
        Assert.Same(fixer, resolved);
        Assert.Equal(["bash.unquoted-vars"], registry.RegisteredRuleIds);
    }

    [Fact]
    public void Registry_rejects_duplicate_rule_ids()
        => Assert.Throws<InvalidOperationException>(
            () => new FindingFixerRegistry([new BashSetEMissingFixer(), new BashSetEMissingFixer()]));

    [Fact]
    public void Registry_rejects_duplicate_rule_ids_case_insensitively()
        => Assert.Throws<InvalidOperationException>(
            () => new FindingFixerRegistry(
                [new RuleIdFixer("rule"), new RuleIdFixer("RULE")]));

    private sealed class RuleIdFixer(string ruleId) : IFindingFixer
    {
        public string RuleId { get; } = ruleId;

        public FixProposal? TryPropose(FixContext context) => null;
    }

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

    [Fact]
    public void FixProposal_enforces_command_origin_thread_pairing()
    {
        Assert.Throws<ArgumentException>(() => new FixProposal("f", 1, 1, "x", "r", FixOrigin.LlmCommanded));
        Assert.Throws<ArgumentException>(() => new FixProposal("f", 1, 1, "x", "r", FixOrigin.LlmCommanded, 0));
        Assert.Throws<ArgumentException>(() => new FixProposal("f", 1, 1, "x", "r", FixOrigin.Deterministic, 7));
        _ = new FixProposal("f", 1, 1, "x", "r", FixOrigin.LlmCommanded, 1);
    }
}
