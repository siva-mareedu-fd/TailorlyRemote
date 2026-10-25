using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Tailorly.Docx;

public enum DocxVerdict
{
    Ok,

    /// <summary>Not a Word .docx (PDF, legacy .doc, anything else). Maps to 415 UNSUPPORTED_FILE_TYPE.</summary>
    UnsupportedType,

    /// <summary>Over the compressed size limit. Maps to 413 FILE_TOO_LARGE.</summary>
    TooLarge,

    /// <summary>A .docx we refuse to process (macros, OLE, external links, password, archive limits). 422 FILE_UNSAFE.</summary>
    Unsafe,
}

/// <param name="Reason">Stable machine reason (e.g. <c>macros</c>, <c>zip_bomb</c>); safe to log.</param>
/// <param name="Message">User-facing sentence. Never contains file content.</param>
public sealed record DocxInspection(DocxVerdict Verdict, string Reason, string Message)
{
    public static readonly DocxInspection Ok = new(DocxVerdict.Ok, "ok", "");
    public bool IsOk => Verdict == DocxVerdict.Ok;
}

/// <summary>
/// Checks an uploaded file before any OpenXML parsing (SDD §10.5, threat model §14.2). Works on the raw zip and XML with
/// DTD processing prohibited, never trusts the sizes in zip headers (it counts decompressed bytes as it reads) and
/// stops at the first problem.
/// </summary>
public static class DocxSafetyInspector
{
    public const string PdfMessage = "Please upload the Word (.docx) version of your résumé.";

    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] PdfMagic = "%PDF"u8.ToArray();
    private static readonly byte[] OleMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] EncryptedPackageUtf16 = Encoding.Unicode.GetBytes("EncryptedPackage");

    private static readonly string[] ArchiveExtensions =
        [".zip", ".docx", ".docm", ".dotx", ".dotm", ".xlsx", ".xlsm", ".pptx", ".pptm", ".jar", ".7z", ".rar", ".gz", ".tgz", ".tar", ".cab", ".odt"];

    private const string WordMainContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    private const string WordTemplateContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml";
    private const string HyperlinkRelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";

    private static DocxInspection Unsupported(string reason, string message) => new(DocxVerdict.UnsupportedType, reason, message);
    private static DocxInspection Unsafe(string reason, string message) => new(DocxVerdict.Unsafe, reason, message);

    private static readonly DocxInspection NotDocx =
        Unsupported("not_docx", "This file is not a Word (.docx) document. Please upload the .docx version of your résumé.");

    public static DocxInspection Inspect(string fileName, ReadOnlyMemory<byte> content)
    {
        var bytes = content.Span;
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();

        if (bytes.StartsWith(PdfMagic) || ext == ".pdf")
            return Unsupported("pdf", PdfMessage);
        if (ext is ".docm" or ".dotm")
            return Unsafe("macros", "Files with macros (.docm) are not accepted. Save the résumé as a regular .docx and try again.");
        if (content.Length > DocxLimits.MaxCompressedBytes)
            return new DocxInspection(DocxVerdict.TooLarge, "too_large", "The file is larger than 5 MB.");

        if (bytes.StartsWith(OleMagic))
        {
            // An encrypted (password-protected) .docx is an OLE compound file holding an "EncryptedPackage" stream.
            return bytes.IndexOf(EncryptedPackageUtf16) >= 0
                ? Unsafe("password_protected", "This file is password-protected. Remove the password in Word, save, and upload it again.")
                : Unsupported("legacy_doc", "This looks like an old Word (.doc) file. Open it in Word, save it as .docx, and upload that.");
        }
        if (ext is not ".docx")
            return NotDocx;
        if (!bytes.StartsWith(ZipMagic))
            return NotDocx;

        try
        {
            return InspectPackage(content);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure to read an untrusted archive (bad headers, truncated data) means "not a readable .docx".
            return Unsupported("corrupt", "We could not read this file. Open it in Word, save it again as .docx, and upload that.");
        }
    }

    private static DocxInspection InspectPackage(ReadOnlyMemory<byte> content)
    {
        using var stream = new ReadOnlyMemoryStream(content);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        if (zip.Entries.Count > DocxLimits.MaxZipEntries)
            return Unsafe("too_many_entries", "This file has an unusual internal structure and was rejected for safety.");

        long declared = 0;
        foreach (var e in zip.Entries)
            declared += e.Length;
        if (declared > DocxLimits.MaxUncompressedBytes)
            return Unsafe("zip_bomb", "This file expands to more than 25 MB and was rejected for safety.");

        var hasContentTypes = false;
        var hasWordMain = false;
        long budget = DocxLimits.MaxUncompressedBytes;

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var lower = name.ToLowerInvariant();

            if (lower.EndsWith("vbaproject.bin", StringComparison.Ordinal) || lower.EndsWith("vbadata.xml", StringComparison.Ordinal))
                return Unsafe("macros", "Files with macros are not accepted. Save the résumé as a regular .docx and try again.");
            if (lower.Contains("/embeddings/", StringComparison.Ordinal) || lower.Contains("oleobject", StringComparison.Ordinal) ||
                lower.Contains("/activex/", StringComparison.Ordinal))
                return Unsafe("embedded_object", "This file contains embedded objects (for example a spreadsheet or OLE object). Remove them and upload again.");
            if (ArchiveExtensions.Any(x => lower.EndsWith(x, StringComparison.Ordinal)))
                return Unsafe("nested_archive", "This file contains another archive inside it and was rejected for safety.");
            if (name.EndsWith('/')) continue; // directory entry

            var data = ReadBounded(entry, ref budget);
            if (data is null)
                return Unsafe("zip_bomb", "This file expands to more than 25 MB and was rejected for safety.");

            if (data.AsSpan().StartsWith(ZipMagic))
                return Unsafe("nested_archive", "This file contains another archive inside it and was rejected for safety.");
            if (data.AsSpan().StartsWith(OleMagic))
                return Unsafe("embedded_object", "This file contains embedded objects (for example a spreadsheet or OLE object). Remove them and upload again.");

            if (!lower.EndsWith(".xml", StringComparison.Ordinal) && !lower.EndsWith(".rels", StringComparison.Ordinal))
                continue;

            DocxInspection? problem;
            try
            {
                problem = lower switch
                {
                    "[content_types].xml" => CheckContentTypes(data, out hasWordMain),
                    _ when lower.EndsWith(".rels", StringComparison.Ordinal) => CheckRelationships(data),
                    "word/settings.xml" => CheckSettings(data),
                    _ => CheckWellFormed(data),
                };
            }
            catch (XmlException)
            {
                // Includes any DOCTYPE (DtdProcessing.Prohibit): external entities are never resolved.
                return Unsafe("bad_xml", "This file contains XML we do not accept and was rejected for safety.");
            }
            if (lower == "[content_types].xml") hasContentTypes = true;
            if (problem is not null) return problem;
        }

        if (!hasContentTypes || !hasWordMain)
            return NotDocx;
        return DocxInspection.Ok;
    }

    /// <summary>Decompresses one entry, counting real bytes against the shared budget. Null when the budget runs out.</summary>
    private static byte[]? ReadBounded(ZipArchiveEntry entry, ref long budget)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            budget -= read;
            if (budget < 0) return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static XmlReader CreateReader(byte[] data) => XmlReader.Create(new MemoryStream(data), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        MaxCharactersFromEntities = 0,
    });

    private static DocxInspection? CheckWellFormed(byte[] data)
    {
        using var r = CreateReader(data);
        while (r.Read()) { }
        return null;
    }

    private static DocxInspection? CheckContentTypes(byte[] data, out bool hasWordMain)
    {
        hasWordMain = false;
        using var r = CreateReader(data);
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            var ct = r.GetAttribute("ContentType") ?? "";
            if (ct.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) || ct.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
                return Unsafe("macros", "Files with macros are not accepted. Save the résumé as a regular .docx and try again.");
            if (ct.Contains("oleObject", StringComparison.OrdinalIgnoreCase) || ct.Contains("activeX", StringComparison.OrdinalIgnoreCase))
                return Unsafe("embedded_object", "This file contains embedded objects (for example a spreadsheet or OLE object). Remove them and upload again.");
            // The main part may be declared by an Override or by a Default for its extension (both valid OPC).
            if (r.LocalName is "Override" or "Default" && ct is WordMainContentType or WordTemplateContentType)
                hasWordMain = true;
        }
        return null;
    }

    private static DocxInspection? CheckRelationships(byte[] data)
    {
        using var r = CreateReader(data);
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || r.LocalName != "Relationship") continue;
            var mode = r.GetAttribute("TargetMode");
            var type = r.GetAttribute("Type") ?? "";
            // Hyperlinks (LinkedIn, portfolio) are the only external targets a résumé needs. Linked templates, images,
            // frames and sub-documents would make Word fetch remote content.
            if (string.Equals(mode, "External", StringComparison.OrdinalIgnoreCase) && type != HyperlinkRelType)
                return Unsafe("external_relationship", "This file links to external content (such as a remote template or image). Remove the link and upload again.");
        }
        return null;
    }

    private static DocxInspection? CheckSettings(byte[] data)
    {
        using var r = CreateReader(data);
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            if (r.LocalName == "writeProtection")
                return Unsafe("password_protected", "This file is password-protected. Remove the password in Word, save, and upload it again.");
            if (r.LocalName == "documentProtection")
            {
                var enforced = r.GetAttribute("enforcement", r.NamespaceURI);
                if (enforced is "1" or "true" or "on")
                    return Unsafe("password_protected", "This file has editing restrictions. Remove the protection in Word, save, and upload it again.");
            }
        }
        return null;
    }

    /// <summary>Minimal read-only stream over a <see cref="ReadOnlyMemory{T}"/> (no copy).</summary>
    private sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> data) : Stream
    {
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => _pos = value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_pos >= data.Length) return 0;
            var n = (int)Math.Min(buffer.Length, data.Length - _pos);
            data.Span.Slice((int)_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => _pos = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => data.Length + offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
