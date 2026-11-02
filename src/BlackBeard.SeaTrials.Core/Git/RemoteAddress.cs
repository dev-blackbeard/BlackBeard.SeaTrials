namespace BlackBeard.SeaTrials.Core.Git;

/// <summary>
/// Puts the address of a git remote in a form where the same repository compares equal however it was written:
/// https, ssh or the short scp form, with or without ".git" or a trailing slash, with or without a user name, in any
/// case for the host. The path is compared exactly: the same name under another owner is another repository.
/// </summary>
public static class RemoteAddress
{
    public static string Normalize(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var text = address.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var kind = text.Substring(0, scheme).ToLowerInvariant();
            var rest = text.Substring(scheme + 3);
            if (kind == "file")
            {
                return LocalPath(rest);
            }

            var slash = rest.IndexOf('/');
            var authority = slash < 0 ? rest : rest.Substring(0, slash);
            var path = slash < 0 ? "" : rest.Substring(slash + 1);
            var at = authority.LastIndexOf('@');
            var host = (at < 0 ? authority : authority.Substring(at + 1)).ToLowerInvariant();
            return Join(host, path);
        }

        if (LooksLikeScpAddress(text, out var colon))
        {
            var left = text.Substring(0, colon);
            var at = left.LastIndexOf('@');
            var host = (at < 0 ? left : left.Substring(at + 1)).ToLowerInvariant();
            return Join(host, text.Substring(colon + 1));
        }

        return LocalPath(text);
    }

    public static bool AreSame(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    /// <summary>The repository's name: the last part of the address without ".git". Safe to use as a folder name.</summary>
    public static string RepositoryName(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = Normalize(address);
        var name = normalized.Substring(normalized.LastIndexOf('/') + 1);
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_').ToArray()).Trim('.');
        return safe.Length == 0 ? "repository" : safe;
    }

    // "user@host:path", but not "C:\folder" or "C:/folder", and not anything with a slash before the first colon.
    private static bool LooksLikeScpAddress(string text, out int colon)
    {
        colon = text.IndexOf(':');
        if (colon <= 1)
        {
            return false;
        }

        var slash = text.IndexOfAny(new[] { '/', '\\' });
        return slash < 0 || slash > colon;
    }

    private static string Join(string host, string path)
    {
        var cleaned = path.Replace('\\', '/').Trim('/');
        if (cleaned.EndsWith(".git", StringComparison.Ordinal))
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 4);
        }

        return host + "/" + cleaned.TrimEnd('/');
    }

    private static string LocalPath(string text)
    {
        var path = text.Replace('\\', '/');
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
        {
            path = path.Substring(1);
        }

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.Ordinal))
        {
            path = path.Substring(0, path.Length - 4);
        }

        if (OperatingSystem.IsWindows())
        {
            path = path.ToLowerInvariant();
        }

        return "file:" + path;
    }
}
