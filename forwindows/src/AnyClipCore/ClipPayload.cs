namespace AnyClip.Core;

public abstract record ClipPayload
{
    public abstract string Kind { get; }
    public abstract string PayloadHash { get; }
}

public sealed record TextClip(string Text) : ClipPayload
{
    public override string Kind => "text";
    public override string PayloadHash => Hashing.Sha256Hex(Text);
}

public sealed record ImageClip(byte[] Png) : ClipPayload
{
    public override string Kind => "image";
    public override string PayloadHash => Hashing.Sha256Hex(Png);
}

public sealed record FileClip(string Name, byte[] Data) : ClipPayload
{
    public override string Kind => "file";
    public override string PayloadHash => Hashing.Sha256Hex(Data);
}

/// One file in a kind:"files" clip. RelPath is the wire "path": the file's
/// POSIX-separated path relative to the copied selection, top folder name
/// INCLUDED (e.g. "docs/q3/report.txt"), or null for a file the user selected
/// directly — those stay byte-identical to a pre-1.3 entry on the wire.
/// Pinned cross-implementation shape: Python (name, data, relpath|None),
/// Swift (name: String, data: Data, relPath: String?).
public sealed record FileEntry(string Name, byte[] Data, string? RelPath = null);

public sealed record FilesClip(IReadOnlyList<FileEntry> Files) : ClipPayload
{
    /// Loose-file convenience: every entry gets RelPath null. Keeps the
    /// (name, bytes) call sites that never deal with folders unchanged.
    public FilesClip(IReadOnlyList<(string Name, byte[] Data)> files)
        : this(files.Select(f => new FileEntry(f.Name, f.Data)).ToList()) { }

    public override string Kind => "files";
    /// Content only — tree and flat delivery of the same bytes must produce
    /// the same echo-suppression key.
    public override string PayloadHash =>
        Hashing.AggregateFilesHash(Files.Select(f => Hashing.Sha256Hex(f.Data)));
}
