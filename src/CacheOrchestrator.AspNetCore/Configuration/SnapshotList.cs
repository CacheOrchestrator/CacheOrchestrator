using System.Collections;

namespace CacheOrchestrator.Configuration;

/// <summary>Copies caller-owned input once and exposes no mutable public collection surface.</summary>
internal sealed class SnapshotList<T>(IReadOnlyList<T> values) : IReadOnlyList<T>
{
    // Internal cache processing uses the prepared array without enumeration boxing or copying.
    internal T[] Values { get; } = values.ToArray();
    public int Count => Values.Length;
    public T this[int index] => Values[index];
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Values).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
