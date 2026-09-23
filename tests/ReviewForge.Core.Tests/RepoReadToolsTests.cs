using ReviewForge.Core.Reasoning;
using Xunit;

namespace ReviewForge.Core.Tests;

public class RepoReadToolsTests : IDisposable
{
    private readonly string _Root;

    public RepoReadToolsTests()
    {
        _Root = Path.Combine(Path.GetTempPath(), "reviewforge-repotools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_Root, "src"));
        Directory.CreateDirectory(Path.Combine(_Root, ".git"));
        File.WriteAllLines(Path.Combine(_Root, "src", "A.cs"), ["one", "two TARGET", "three"]);
        File.WriteAllText(Path.Combine(_Root, "src", "bin.dat"), "a\0b");
        File.WriteAllText(Path.Combine(_Root, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(_Root, ".git", "config"), "gitconfig");
        File.WriteAllText(Path.Combine(_Root, "cert.pem"), "pem");
    }

    public void Dispose() => Directory.Delete(_Root, recursive: true);

    private RepoReadTools Tools(int maxLines = RepoReadTools.DefaultMaxLines) => new(_Root, maxLines: maxLines);

    [Fact]
    public void List_hides_denied_entries()
    {
        var listing = Tools().List();
        Assert.Contains("src", listing);
        Assert.DoesNotContain(".git", listing);
        Assert.DoesNotContain(".env", listing);
        Assert.DoesNotContain("cert.pem", listing);
    }

    [Fact]
    public void List_subdirectory_and_empty()
    {
        Assert.Contains("src/A.cs", Tools().List("src"));
        Directory.CreateDirectory(Path.Combine(_Root, "empty"));
        Assert.Equal("(empty)", Tools().List("empty"));
    }

    [Fact]
    public void List_missing_directory()
        => Assert.Contains("not a directory", Tools().List("nope"));

    [Fact]
    public void ReadFile_numbers_lines()
    {
        var content = Tools().ReadFile("src/A.cs");
        Assert.Contains("1: one", content);
        Assert.Contains("3: three", content);
    }

    [Fact]
    public void ReadFile_slices_and_marks_more()
    {
        var content = Tools().ReadFile("src/A.cs", startLine: 2, maxLines: 1);
        Assert.Contains("2: two TARGET", content);
        Assert.Contains("more lines", content);
    }

    [Fact]
    public void ReadFile_out_of_range_and_missing()
    {
        Assert.Contains("out of range", Tools().ReadFile("src/A.cs", startLine: 99));
        Assert.Contains("not found", Tools().ReadFile("src/Missing.cs"));
    }

    [Fact]
    public void ReadFile_refuses_binary_and_denied()
    {
        Assert.Contains("binary", Tools().ReadFile("src/bin.dat"));
        Assert.Contains("denied", Tools().ReadFile(".env"));
        Assert.Contains("denied", Tools().ReadFile(".git/config"));
        Assert.Contains("denied", Tools().ReadFile("cert.pem"));
    }

    [Theory]
    [InlineData("id_rsa")]
    [InlineData("id_ed25519")]
    [InlineData("certs/app.pfx")]
    [InlineData("signing/key.p12")]
    [InlineData("strongname.snk")]
    [InlineData(".kube/config")]
    [InlineData("cluster.kubeconfig")]
    [InlineData(".aws/credentials")]
    [InlineData(".npmrc")]
    [InlineData("appsettings.Production.json")]
    public void ReadFile_denies_private_keys(string path)
    {
        Assert.StartsWith("access denied", Tools().ReadFile(path));
    }

    [Fact]
    public void ReadFile_allows_base_appsettings()
    {
        File.WriteAllText(Path.Combine(_Root, "appsettings.json"), "{\"x\": 1}");
        Assert.DoesNotContain("denied", Tools().ReadFile("appsettings.json"));
    }

    [Fact]
    public void Grep_skips_denied_files()
    {
        File.WriteAllText(Path.Combine(_Root, "appsettings.Production.json"), "SECRETVALUE=xyz");
        Assert.Equal("no matches", Tools().Grep("SECRETVALUE"));
    }

    [Fact]
    public void Escape_outside_root_is_denied()
    {
        Assert.Contains("denied", Tools().ReadFile("../outside.txt"));
        Assert.Contains("denied", Tools().ReadFile("../../etc/passwd"));
    }

    [Fact]
    public void Escape_via_sibling_prefix_is_denied()
    {
        var sibling = _Root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(sibling, "leak.txt"), "x");
            Assert.Contains("denied", Tools().ReadFile("../" + Path.GetFileName(sibling) + "/leak.txt"));
            Assert.Contains("denied", Tools().List("../" + Path.GetFileName(sibling)));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void Symlink_escaping_the_root_is_denied()
    {
        var outside = Path.Combine(Path.GetTempPath(), "reviewforge-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "top secret");
        try
        {
            var link = Path.Combine(_Root, "linked");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (UnauthorizedAccessException)
            {
                return; // symlinks unavailable (non-elevated Windows)
            }
            catch (IOException)
            {
                return;
            }

            // Lexically inside the root, but resolving outside — must be refused on every entry point.
            Assert.Contains("denied", Tools().ReadFile("linked/secret.txt"));
            Assert.Contains("denied", Tools().List("linked"));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Grep_finds_matches_with_location()
    {
        var result = Tools().Grep("TARGET");
        Assert.Contains("src/A.cs:2", result);
    }

    [Fact]
    public void Grep_no_matches_and_invalid_pattern()
    {
        Assert.Equal("no matches", Tools().Grep("zzz-not-there"));
        Assert.Contains("invalid pattern", Tools().Grep("(unclosed"));
    }

    [Fact]
    public void Grep_skips_denied_and_binary()
    {
        var result = Tools().Grep("SECRET|gitconfig|a");
        Assert.DoesNotContain(".env", result);
        Assert.DoesNotContain(".git", result);
    }

    [Fact]
    public void Grep_with_glob_and_subdir()
    {
        Assert.Contains("A.cs", Tools().Grep("one", path: "src", glob: "*.cs"));
        Assert.Contains("not a directory", Tools().Grep("x", path: "nope"));
    }

    [Fact]
    public void Custom_deny_patterns_extend_defaults()
    {
        var tools = new RepoReadTools(_Root, denyPatterns: [@"A\.cs$"]);
        Assert.Contains("denied", tools.ReadFile("src/A.cs"));
    }

    [Fact]
    public void Grep_skips_excluded_directories()
    {
        Directory.CreateDirectory(Path.Combine(_Root, "bin"));
        Directory.CreateDirectory(Path.Combine(_Root, "node_modules", "pkg"));
        Directory.CreateDirectory(Path.Combine(_Root, "obj"));
        File.WriteAllText(Path.Combine(_Root, "src", "app.cs"), "NEEDLE in src");
        File.WriteAllText(Path.Combine(_Root, "bin", "app.cs"), "NEEDLE in bin");
        File.WriteAllText(Path.Combine(_Root, "node_modules", "pkg", "index.js"), "NEEDLE in node_modules");
        File.WriteAllText(Path.Combine(_Root, "obj", "x.cs"), "NEEDLE in obj");

        var result = Tools().Grep("NEEDLE");

        Assert.Contains("src/app.cs", result);
        Assert.DoesNotContain("bin/", result);
        Assert.DoesNotContain("node_modules", result);
        Assert.DoesNotContain("obj/", result);
    }

    [Fact]
    public void Grep_exclude_dirs_are_overridable()
    {
        Directory.CreateDirectory(Path.Combine(_Root, "bin"));
        Directory.CreateDirectory(Path.Combine(_Root, "gen"));
        File.WriteAllText(Path.Combine(_Root, "bin", "x.cs"), "NEEDLE in bin");
        File.WriteAllText(Path.Combine(_Root, "gen", "y.cs"), "NEEDLE in gen");

        var tools = new RepoReadTools(_Root, excludeDirs: ["gen"]);
        var result = tools.Grep("NEEDLE");

        Assert.Contains("bin/x.cs", result); // no longer excluded
        Assert.DoesNotContain("gen/", result);
    }

    [Fact]
    public void Grep_skips_files_over_size_cap()
    {
        var big = Path.Combine(_Root, "src", "big.cs");
        using (var fs = new FileStream(big, FileMode.Create))
        {
            fs.SetLength(2 * 1024 * 1024); // 2 MiB sparse file
        }

        File.WriteAllText(big, new string('a', 1024 * 1024) + "NEEDLE" + new string('b', 1024 * 1024));

        Assert.Equal("no matches", Tools().Grep("NEEDLE"));
    }

    [Fact]
    public void ReadFile_slice_matches_previous_contract()
    {
        var big = Path.Combine(_Root, "big.txt");
        File.WriteAllLines(big, Enumerable.Range(1, 5000).Select(i => $"line {i}"));

        var content = Tools().ReadFile("big.txt", startLine: 2500, maxLines: 100);

        Assert.Contains("2500: line 2500", content);
        Assert.Contains("2599: line 2599", content);
        Assert.DoesNotContain("2600: line 2600", content);
        Assert.Contains("2401 more lines", content);
    }

    [Fact]
    public void Grep_does_not_follow_symlinked_directory_outside_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "reviewforge-grepdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "leak.txt"), "NEEDLE secret");
        try
        {
            var link = Path.Combine(_Root, "linked");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            Assert.Equal("no matches", Tools().Grep("NEEDLE"));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Grep_refuses_symlink_file_pointing_outside_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "reviewforge-grepfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secret, "NEEDLE secret");
        try
        {
            var link = Path.Combine(_Root, "src", "link.txt");
            try
            {
                File.CreateSymbolicLink(link, secret);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            Assert.Equal("no matches", Tools().Grep("NEEDLE"));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}