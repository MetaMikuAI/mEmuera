using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace mEmuera.Contracts;

public static class IdentityJson
{
    public static JsonSerializerOptions Options { get; } = new() { Converters = { new StableCollections() } };
    private static string Key<T>(T value) => value is IFormattable format ? format.ToString(null, CultureInfo.InvariantCulture) : value!.ToString()!;
    private sealed class StableCollections : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>) || type.GetGenericTypeDefinition() == typeof(ImmutableHashSet<>));
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance((type.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>) ? typeof(StableDictionary<,>) : typeof(StableSet<>))
                .MakeGenericType(type.GenericTypeArguments))!;
    }
    private sealed class StableDictionary<TKey, TValue> : JsonConverter<ImmutableDictionary<TKey, TValue>> where TKey : notnull
    {
        public override ImmutableDictionary<TKey, TValue> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, ImmutableDictionary<TKey, TValue> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var pair in value.OrderBy(p => Key(p.Key), StringComparer.Ordinal))
            { writer.WritePropertyName(Key(pair.Key)); JsonSerializer.Serialize(writer, pair.Value, options); }
            writer.WriteEndObject();
        }
    }
    private sealed class StableSet<T> : JsonConverter<ImmutableHashSet<T>>
    {
        public override ImmutableHashSet<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, ImmutableHashSet<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value.OrderBy(Key, StringComparer.Ordinal)) JsonSerializer.Serialize(writer, item, options);
            writer.WriteEndArray();
        }
    }
}
