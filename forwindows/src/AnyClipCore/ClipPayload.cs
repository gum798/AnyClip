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
