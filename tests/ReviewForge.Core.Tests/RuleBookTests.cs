using ReviewForge.Core.Reasoning;
using ReviewForge.Core.Reasoning.Rules;
using Xunit;

namespace ReviewForge.Core.Tests;

public sealed class RuleBookTests
{
    [Fact]
    public void Composer_activates_language_and_always_packs()
    {
        var book = new RuleBookComposer().Compose(["src/app.component.ts"], []);
        Assert.Contains(book.Packs, p => p.Id == "general");
        Assert.Contains(book.Packs, p => p.Id == "performance");
        Assert.Contains(book.Packs, p => p.Id == "security");
        Assert.Contains(book.Packs, p => p.Id == "angular");
        Assert.Contains(book.Packs, p => p.Id == "typescript");
        Assert.DoesNotContain(book.Packs, p => p.Id == "python");
    }

    [Fact]
    public void Composer_activates_root_file_and_path_patterns()
    {
        var book = new RuleBookComposer().Compose(["mongodb/query.js", "Dockerfile.prod"], ["angular.json"]);
        Assert.Contains(book.Packs, p => p.Id == "mongodb");
        Assert.Contains(book.Packs, p => p.Id == "dockerfile");
        Assert.Contains(book.Packs, p => p.Id == "angular");
    }

    [Fact]
    public void Composer_applies_rule_override_and_stable_hash()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "override.json"),
            "{\"id\":\"general\",\"title\":\"x\",\"version\":2,\"activation\":{\"always\":true,\"extensions\":[],\"pathPatterns\":[],\"rootFiles\":[]},\"rules\":[{\"id\":\"general.other\",\"title\":\"Changed\",\"description\":\"d\",\"category\":\"bug\",\"defaultSeverity\":\"high\",\"enabled\":true}]}");
        try
        {
            var composer = new RuleBookComposer();
            var first = composer.Compose([], [], path);
            var second = composer.Compose([], [], path);
            Assert.Equal(first.VersionHash, second.VersionHash);
            Assert.Equal("Changed", first.Rules["general.other"].Title);
            Assert.Equal("high", first.Rules["general.other"].DefaultSeverity);
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    [Fact]
    public void ReviewTools_rejects_unknown_and_accepts_catch_all()
    {
        var book = new RuleBookComposer().Compose([], []);
        var collector = new ReviewCollector();
        var tools = new ReviewTools(collector, new ContextStore(), book);
        var rejected = tools.RecordFinding("not.active", "t", "low", "bug", "d");
        Assert.Contains("general.other", rejected);
        Assert.Contains("unknown rule", rejected);
        Assert.StartsWith("recorded finding", tools.RecordFinding("general.other", "t", "low", "bug", "d"));
        Assert.Contains("general.other", tools.GetRulebook());
        Assert.Contains("general.other", tools.GetRulebook("general"));
    }

    [Fact]
    public void Composer_extends_with_new_pack_and_rejects_missing_catch_all()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(path);
        try
        {
            File.WriteAllText(Path.Combine(path, "new.json"),
                "{\"id\":\"custom\",\"title\":\"Custom\",\"version\":1,\"activation\":{\"always\":true,\"extensions\":[],\"pathPatterns\":[],\"rootFiles\":[]},\"rules\":[{\"id\":\"custom.rule\",\"title\":\"Rule\",\"description\":\"d\",\"category\":\"docs\",\"defaultSeverity\":\"info\",\"enabled\":true}]}");
            var extended = new RuleBookComposer().Compose([], [], path);
            Assert.Contains(extended.Packs, p => p.Id == "custom");
            Assert.NotEqual(new RuleBookComposer().Compose([], []).VersionHash, extended.VersionHash);

            File.WriteAllText(Path.Combine(path, "bad.json"),
                "{\"id\":\"general\",\"title\":\"General\",\"version\":1,\"activation\":{\"always\":true,\"extensions\":[],\"pathPatterns\":[],\"rootFiles\":[]},\"rules\":[{\"id\":\"general.other\",\"title\":\"Other\",\"description\":\"d\",\"category\":\"bug\",\"defaultSeverity\":\"medium\",\"enabled\":false}]}");
            Assert.Throws<InvalidOperationException>(() => new RuleBookComposer().Compose([], [], path));
        }
        finally
        {
            Directory.Delete(path, true);
        }
    }

    [Fact]
    public void Prompt_contains_index_without_rule_descriptions()
    {
        var book = new RuleBookComposer().Compose(["a.cs"], []);
        var prompt = SystemPromptComposer.Compose(ruleBook: book);
        Assert.Contains("csharp.null-deref", prompt);
        Assert.DoesNotContain("Dereference of a value that can be null", prompt);
    }
}