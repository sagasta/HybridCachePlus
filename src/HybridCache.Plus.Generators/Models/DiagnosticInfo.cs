using Microsoft.CodeAnalysis;

namespace HybridCache.Plus.Generators.Models;

public sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
{
    public DiagnosticDescriptor Descriptor { get; }
    public string? FilePath { get; }
    public int Start { get; }
    public int Length { get; }
    public EquatableArray<string> MessageArgs { get; }

    public DiagnosticInfo(
        DiagnosticDescriptor descriptor,
        Location? location,
        params string[] messageArgs)
    {
        Descriptor = descriptor;
        if (location != null && location.IsInSource)
        {
            FilePath = location.SourceTree?.FilePath;
            Start = location.SourceSpan.Start;
            Length = location.SourceSpan.Length;
        }
        MessageArgs = [with(messageArgs)];
    }

    public Diagnostic ToDiagnostic()
    {
        Location location;
        if (!string.IsNullOrEmpty(FilePath))
        {
            var textSpan = new Microsoft.CodeAnalysis.Text.TextSpan(Start, Length);
            location = Location.Create(FilePath!, textSpan, new Microsoft.CodeAnalysis.Text.LinePositionSpan());
        }
        else
        {
            location = Location.None;
        }

        var args = new object[MessageArgs.Count];
        for (var i = 0; i < MessageArgs.Count; i++)
        {
            args[i] = MessageArgs[i];
        }

        return Diagnostic.Create(Descriptor, location, args);
    }

    public bool Equals(DiagnosticInfo? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Descriptor.Id == other.Descriptor.Id &&
               FilePath == other.FilePath &&
               Start == other.Start &&
               Length == other.Length &&
               MessageArgs.Equals(other.MessageArgs);
    }

    public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);
    public override int GetHashCode() => (Descriptor.Id, FilePath, Start, Length).GetHashCode();
}
