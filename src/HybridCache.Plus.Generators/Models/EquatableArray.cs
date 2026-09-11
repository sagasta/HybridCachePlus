using System.Collections;

namespace HybridCache.Plus.Generators.Models;

/// <summary>
/// An immutable array wrapper that provides value equality semantics for Roslyn incremental pipelines.
/// </summary>
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly T[]? _array;

    public EquatableArray(T[]? array) => _array = array;
    public EquatableArray(IEnumerable<T>? items) => _array = items?.ToArray();

    public static EquatableArray<T> Empty => [with([])];

    public int Count => _array?.Length ?? 0;

    public T this[int index] => _array != null ? _array[index] : throw new IndexOutOfRangeException();

    public bool Equals(EquatableArray<T> other)
    {
        if (_array == other._array) return true;
        if (_array == null || other._array == null) return false;
        if (_array.Length != other._array.Length) return false;

        return !_array.Where((t, i) => !t.Equals(other._array[i])).Any();
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        return _array == null ? 0 : _array.Aggregate(17, (current, item) => current * 31 + (item?.GetHashCode() ?? 0));
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);
    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_array ?? [])).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
