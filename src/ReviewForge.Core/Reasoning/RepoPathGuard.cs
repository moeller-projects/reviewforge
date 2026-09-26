using System.Text.RegularExpressions;
using ReviewForge.Core.Analysis;

namespace ReviewForge.Core.Reasoning;

/// <summary>
/// Shared path policy for checkout access: root normalization, lexical + real-path
/// containment (<see cref="PathSafety"/>), and the deny-regex for secrets and
/// credentials. Extracted from <see cref="RepoReadTools"/> so that
/// <see cref="HashLineEditor"/> applies byte-identical rules without duplication.
/// </summary>
public sealed class RepoPathGuard
{
    private static readonly string[] DefaultDenyPatterns =
    [
        @"\.git(/|$)",
        @"\.env($|\.)", @"\.envrc$",
        @"\.pem$", @"\.key$", @"\.pfx$", @"\.p12$", @"\.snk$",
        @"(^|/)id_(rsa|dsa|ecdsa|ed25519)$",
        @"(^|/)\.kube/config$|(^|/|\.)kubeconfig$",
        @"(^|/)\.aws/",                     // AWS credentials & config
        @"(^|/)\.npmrc$", @"(^|/)\.pypirc$", // registry tokens
        @"(^|/)appsettings\.[^/]+\.json$",  // environment-specific settings (base appsettings.json stays readable)
        @"secrets", @"credentials",
    ];

    private readonly Regex[] _Deny;

    public RepoPathGuard(string rootDir, IEnumerable<string>? denyPatterns = null)
    {
        Root = Path.GetFullPath(rootDir);
        _Deny =
        [
            .. (denyPatterns ?? DefaultDenyPatterns)
                .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
        ];
    }

    /// <summary>Normalized absolute root of the contained checkout.</summary>
    public string Root { get; }

    /// <summary>Resolves the root once, returning the real path and its symlink depth.</summary>
    public (string RootReal, int RootLinks) ResolveRoot()
    {
        var rootReal = PathSafety.ResolveReal(Root, out var rootLinks);
        return (rootReal, rootLinks);
    }

    /// <summary>Resolves a repo-relative path to an absolute path inside the root; null + error when denied or escaping.</summary>
    public string? Resolve(string? relativePath, out string? error)
    {
        error = null;
        var rel = (relativePath ?? string.Empty).Replace('\\', '/').TrimStart('/');

        if (IsDenied(rel))
        {
            error = $"access denied: {relativePath}";
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(Root, rel));
        var rootReal = PathSafety.ResolveReal(Root, out var rootLinks);
        var resolved = PathSafety.ResolveReal(full, out var links);
        // Symlink-aware: a checkout-controlled link that resolves outside the root must be
        // refused even though its lexical path stays under Root.
        if (rel.Length != 0 && !PathContainment.IsContained(rootReal, resolved))
        {
            error = $"access denied: path escapes repository root";
            return null;
        }

        // P1-15: the deny policy binds to the content actually read, not the name it is
        // reached by — a committed symlink to a contained-but-denied file must not
        // launder the path past the deny list.
        var resolvedRel = RepoPath.Normalize(Path.GetRelativePath(rootReal, resolved));
        if (IsDenied(resolvedRel))
        {
            error = $"access denied: {relativePath}";
            return null;
        }

        // Symlinks inside the checkout are not traversable: review needs file content, not
        // link semantics (mirrors the enumeration refusal for symlinked directories).
        if (rel.Length != 0 && links > rootLinks)
        {
            error = $"access denied: {relativePath}";
            return null;
        }

        return full;
    }

    /// <summary>True when the file's resolved target escapes the root, matches the deny list,
    /// or the file is reached through a symlink inside the checkout — the deny policy binds
    /// to the content actually read, not the name it is reached by (P1-15).</summary>
    public bool IsResolvedDenied(string fullPath, string rootReal, int rootLinks)
    {
        var resolved = PathSafety.ResolveReal(fullPath, out var links);
        if (!PathContainment.IsContained(rootReal, resolved))
        {
            return true;
        }

        var resolvedRel = RepoPath.Normalize(Path.GetRelativePath(rootReal, resolved));
        return IsDenied(resolvedRel) || links > rootLinks;
    }

    public bool IsDenied(string relativePath)
        => _Deny.Any(d => d.IsMatch(relativePath));
}
