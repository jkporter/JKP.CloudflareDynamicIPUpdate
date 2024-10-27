using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JKP.CloudflareDynamicIPUpdate.Serialization
{
    internal class ScopeConvertor : JsonConverter<Scope>
    {
        public IReadOnlyDictionary<Scope, string> Scopes { get; set; } = ReadOnlyDictionary<Scope, string>.Empty;

        public override Scope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    var value = reader.GetString()!;
                    return Scopes.Single(kv => kv.Value == value).Key;
                case JsonTokenType.Number:
                    return (Scope)reader.GetInt32();
                default:
                    throw new JsonException();
            }
        }

        public override void Write(Utf8JsonWriter writer, Scope value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString().ToLowerInvariant());
        }
    }
}
