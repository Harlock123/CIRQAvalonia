namespace Cirq.Mcp;

/// <summary>
/// The files an agent may open and write: paths are resolved against a root folder and must stay inside it, and an
/// existing file is only written over when the caller says so (or this workspace wrote it). Nothing is ever deleted.
/// </summary>
public sealed class Workspace(string root)
{
    private readonly HashSet<string> _written = new(PathComparer);

    public string Root { get; } = Path.GetFullPath(root);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>A path made absolute against the root; one that leads outside it throws.</summary>
    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is needed.");
        var full = Path.GetFullPath(Path.Combine(Root, path));
        var rootWithSlash = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootWithSlash, comparison))
            throw new ArgumentException($"{path} is outside the working folder ({Root}). Give a path inside it, such as \"circuits/{Path.GetFileName(full)}\".");
        return full;
    }

    /// <summary>A path inside the root with one of <paramref name="extensions"/>, or an error naming them.</summary>
    public string Resolve(string path, string what, params string[] extensions)
    {
        var full = Resolve(path);
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (!extensions.Contains(ext))
            throw new NotSupportedException($"{what} {(extensions.Length == 1 ? "is a" : "is one of")} {string.Join(", ", extensions)} file, not {(ext.Length == 0 ? "a file without an extension" : ext)}.");
        return full;
    }

    /// <summary>
    /// Makes ready to write a file inside the root: refuses one that exists unless <paramref name="overwrite"/> is set
    /// or this workspace wrote it, and makes its folder. Call <see cref="Wrote"/> once it's written.
    /// </summary>
    public void CheckWritable(string fullPath, bool overwrite)
    {
        if (File.Exists(fullPath) && !overwrite && !WroteEarlier(fullPath))
            throw new IOException($"{Path.GetFileName(fullPath)} already exists. Call again with overwrite set to replace it, or choose another name.");
        if (Path.GetDirectoryName(fullPath) is { } dir) Directory.CreateDirectory(dir);
    }

    /// <summary>Notes a file this workspace wrote, so it may write over it again.</summary>
    public void Wrote(string fullPath)
    {
        lock (_written) _written.Add(fullPath);
    }

    private bool WroteEarlier(string fullPath)
    {
        lock (_written) return _written.Contains(fullPath);
    }
}
