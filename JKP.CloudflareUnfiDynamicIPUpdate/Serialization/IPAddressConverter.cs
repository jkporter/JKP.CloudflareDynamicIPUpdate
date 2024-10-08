﻿using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JKP.CloudflareDynamicIPUpdate.Serialization;

internal class IPAddressConverter : JsonConverter<IPAddress>
{
    public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return IPAddress.Parse(reader.GetString() ?? throw new JsonException());
    }

    public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}