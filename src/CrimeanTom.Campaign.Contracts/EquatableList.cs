using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrimeanTom.Campaign.Contracts;

/// <summary>
/// A read-only list that compares by contents rather than by reference.
///
/// Campaign contracts are records, and a record holding an ordinary list
/// compares that list by reference. A weapon read back from a canonical
/// snapshot would then never equal the weapon it was written from, even with
/// identical contents, which quietly breaks round-trip checks and any state
/// comparison built on record equality. Contract fields that hold a sequence
/// use this type so equality keeps meaning what it appears to mean.
/// </summary>
public sealed class EquatableList<T> : IReadOnlyList<T>, IEquatable<EquatableList<T>>
{
    public static readonly EquatableList<T> Empty = new([]);

    private readonly T[] _items;

    public EquatableList(IEnumerable<T> items) => _items = items.ToArray();

    public static implicit operator EquatableList<T>(T[] items) => new(items);

    public T this[int index] => _items[index];

    public int Count => _items.Length;

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(EquatableList<T>? other) =>
        other is not null
        && (ReferenceEquals(this, other) || _items.AsSpan().SequenceEqual(other._items));

    public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }
        return hash.ToHashCode();
    }
}

/// <summary>
/// Reads and writes <see cref="EquatableList{T}"/> as a plain JSON array.
/// System.Text.Json can build a custom collection only when it can add to one
/// after construction, which this type deliberately does not allow.
/// </summary>
public sealed class EquatableListJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType
        && typeToConvert.GetGenericTypeDefinition() == typeof(EquatableList<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(Converter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class Converter<T> : JsonConverter<EquatableList<T>>
    {
        public override EquatableList<T>? Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<T[]>(ref reader, options) is { } items
                ? new EquatableList<T>(items)
                : null;

        public override void Write(
            Utf8JsonWriter writer, EquatableList<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value)
            {
                JsonSerializer.Serialize(writer, item, options);
            }
            writer.WriteEndArray();
        }
    }
}
