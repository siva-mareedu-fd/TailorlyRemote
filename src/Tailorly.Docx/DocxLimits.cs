namespace Tailorly.Docx;

/// <summary>Safety limits for uploaded .docx files (SDD §10.5).</summary>
public static class DocxLimits
{
    public const long MaxCompressedBytes = 5 * 1024 * 1024;
    public const long MaxUncompressedBytes = 25 * 1024 * 1024;
    public const int MaxZipEntries = 500;

    /// <summary>Parsing an accepted file must finish within this time (SDD §10.5).</summary>
    public static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(60);
}
