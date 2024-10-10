using System.Text.Json;
using System.Text.Json.Serialization;
using JsonException = Newtonsoft.Json.JsonException;

namespace JKP.CloudflareDynamicIPUpdate.Serialization
{
    internal class ScopeConvertor : JsonConverter<Scope>
    {
        public Dictionary<int, string> Scopes { get; set; } = new();

        public override Scope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    var value = reader.GetString()!;
                    return (Scope)Scopes.Single(kv => kv.Value == value).Key;
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
