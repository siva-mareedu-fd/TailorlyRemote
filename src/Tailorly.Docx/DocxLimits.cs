namespace Tailorly.Docx;

/// <summary>Safety limits for uploaded .docx files (SDD §10.5). Enforced from M1.</summary>
public static class DocxLimits
{
    public const long MaxCompressedBytes = 5 * 1024 * 1024;
    public const long MaxUncompressedBytes = 25 * 1024 * 1024;
    public const int MaxZipEntries = 500;
}
