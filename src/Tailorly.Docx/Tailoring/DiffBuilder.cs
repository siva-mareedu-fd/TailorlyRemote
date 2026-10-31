using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Tailorly.Docx.Tailoring;

public static class DiffKinds
{
    public const string Equal = "equal";
    public const string Insert = "insert";
    public const string Delete = "delete";
}

/// <param name="Type">equal | insert | delete.</param>
public sealed record DiffSegment(string Type, string Text);

/// <summary>
/// Word-level diff of one paragraph, original against proposed (SDD §10.4), with DiffPlex's word chunker. Adjacent pieces of
/// the same kind are merged. Concatenating the equal and delete segments gives the original; equal and insert, the proposal.
/// </summary>
public static class DiffBuilder
{
    public static IReadOnlyList<DiffSegment> Words(string original, string proposed)
    {
        var model = InlineDiffBuilder.Diff(original, proposed, ignoreWhiteSpace: false, ignoreCase: false, WordChunker.Instance);
        var segments = new List<DiffSegment>();
        foreach (var piece in model.Lines)
        {
            var type = piece.Type switch
            {
                ChangeType.Inserted => DiffKinds.Insert,
                ChangeType.Deleted => DiffKinds.Delete,
                ChangeType.Unchanged => DiffKinds.Equal,
                _ => null,
            };
            if (type is null || string.IsNullOrEmpty(piece.Text)) continue;
            if (segments.Count > 0 && segments[^1].Type == type)
                segments[^1] = segments[^1] with { Text = segments[^1].Text + piece.Text };
            else
                segments.Add(new DiffSegment(type, piece.Text));
        }
        return segments;
    }
}
