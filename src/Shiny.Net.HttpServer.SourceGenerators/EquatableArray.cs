using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Shiny.Net.HttpServer.SourceGenerators;

/// <summary>
/// An <see cref="ImmutableArray{T}"/> with structural equality.
/// <para>
/// Incremental generators cache by comparing the models a pipeline stage produced against the last
/// run. <see cref="ImmutableArray{T}"/> compares by reference, so a model containing one would
/// report "changed" on every keystroke and the cache would never hit. This exists purely to make
/// that comparison meaningful.
/// </para>
/// </summary>
readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    readonly ImmutableArray<T> items = items;

    public static readonly EquatableArray<T> Empty = new(ImmutableArray<T>.Empty);

    public int Count => this.items.IsDefault ? 0 : this.items.Length;

    public T this[int index] => this.items[index];

    public bool Equals(EquatableArray<T> other)
    {
        if (this.items.IsDefault || other.items.IsDefault)
            return this.items.IsDefault && other.items.IsDefault;

        if (this.items.Length != other.items.Length)
            return false;

        for (var i = 0; i < this.items.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(this.items[i], other.items[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && this.Equals(other);

    public override int GetHashCode()
    {
        if (this.items.IsDefault)
            return 0;

        var hash = 17;
        foreach (var item in this.items)
            hash = (hash * 31) + (item?.GetHashCode() ?? 0);

        return hash;
    }

    public IEnumerator<T> GetEnumerator()
        => (this.items.IsDefault ? Enumerable.Empty<T>() : this.items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
}

static class EquatableArrayExtensions
{
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> source) where T : IEquatable<T>
        => new(source as ImmutableArray<T>? ?? ImmutableArray.CreateRange(source));
}
