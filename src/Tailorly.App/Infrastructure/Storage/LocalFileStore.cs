using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Tailorly.Core.Ports;

namespace Tailorly.App.Infrastructure.Storage;

public sealed class StorageOptions
{
    public string Provider { get; set; } = "Local";
    public string RootPath { get; set; } = "storage";
}

/// <summary>
/// Private local-disk storage on a Docker volume. Keys are relative paths validated against a strict pattern and
/// resolved inside the root, so a key can never escape it. No URLs are ever produced.
/// </summary>
public sealed partial class LocalFileStore : IFileStore
{
    private readonly string _root;

    public LocalFileStore(IOptions<StorageOptions> options)
    {
        _root = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await content.CopyToAsync(file, ct);
        File.Move(temp, path, overwrite: true);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        Stream? s = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null;
        return Task.FromResult(s);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        var dir = Resolve(prefix.TrimEnd('/'));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    internal string Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !SafeKey().IsMatch(key) || key.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Invalid storage key.", nameof(key));

        var full = Path.GetFullPath(Path.Combine(_root, key));
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.Ordinal))
            throw new ArgumentException("Invalid storage key.", nameof(key));
        return full;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]{0,255}$")]
    private static partial Regex SafeKey();
}
