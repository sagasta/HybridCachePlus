using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace HybridCache.Plus.Generators.Models;

public sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
{
    public DiagnosticDescriptor Descriptor { get; }
    public string? FilePath { get; }
    public int Start { get; }
    public int Length { get; }
    public LinePositionSpan LineSpan { get; }
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
            LineSpan = location.GetLineSpan().Span;
        }
        MessageArgs = [with(messageArgs)];
    }

    public Diagnostic ToDiagnostic()
    {
        Location location;
        if (!string.IsNullOrEmpty(FilePath))
        {
            location = Location.Create(FilePath!, new TextSpan(Start, Length), LineSpan);
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
