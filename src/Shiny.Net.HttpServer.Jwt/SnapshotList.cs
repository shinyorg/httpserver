using System.Collections;

namespace Shiny.Net.HttpServer.Jwt;

/// <summary>
/// A list that can be changed while it is being read: every write swaps in a new array, and every
/// read works from whichever array was current when it started.
/// </summary>
/// <remarks>
/// The validation parameters are read by every request and written by key rotation, which happens
/// while the server is serving. Behind a plain <see cref="List{T}"/> a rotation landing mid-request
/// threw "collection was modified" out of the validator's loop over the keys, and the request
/// failed with a 500 instead of being authenticated. Writes are rare and small; reads are the hot
/// path and take no lock.
/// </remarks>
sealed class SnapshotList<T> : IList<T>
{
    readonly object sync = new();
    volatile T[] items = [];

    /// <summary>The contents as of now. Never changes once returned; read it once per operation.</summary>
    public T[] Snapshot => this.items;

    public T this[int index]
    {
        get => this.items[index];
        set
        {
            lock (this.sync)
            {
                var copy = (T[])this.items.Clone();
                copy[index] = value;
                this.items = copy;
            }
        }
    }

    public int Count => this.items.Length;

    public bool IsReadOnly => false;

    public void Add(T item)
    {
        lock (this.sync)
            this.items = [.. this.items, item];
    }

    public void Insert(int index, T item)
    {
        lock (this.sync)
        {
            var current = this.items;
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)current.Length, nameof(index));
            this.items = [.. current.AsSpan(0, index), item, .. current.AsSpan(index)];
        }
    }

    public bool Remove(T item)
    {
        lock (this.sync)
        {
            var index = Array.IndexOf(this.items, item);
            if (index < 0)
                return false;

            this.RemoveAtCore(index);
            return true;
        }
    }

    public void RemoveAt(int index)
    {
        lock (this.sync)
            this.RemoveAtCore(index);
    }

    public void Clear()
    {
        lock (this.sync)
            this.items = [];
    }

    public bool Contains(T item) => Array.IndexOf(this.items, item) >= 0;

    public int IndexOf(T item) => Array.IndexOf(this.items, item);

    public void CopyTo(T[] array, int arrayIndex) => this.items.CopyTo(array, arrayIndex);

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)this.items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    // Called under the lock.
    void RemoveAtCore(int index)
    {
        var current = this.items;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)current.Length, nameof(index));
        this.items = [.. current.AsSpan(0, index), .. current.AsSpan(index + 1)];
    }
}
