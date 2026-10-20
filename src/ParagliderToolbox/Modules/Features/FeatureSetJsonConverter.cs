using System.Text.Json;
using System.Text.Json.Serialization;
using ParagliderToolbox.Terrain.Features;

namespace ParagliderToolbox.Modules.Features;

/// <summary>Keeps a <see cref="FeatureSet"/> in a project file as its compressed bytes (base64, <see cref="FeatureFiles.ToBytes"/>).</summary>
public sealed class FeatureSetJsonConverter : JsonConverter<FeatureSet>
{
    /// <inheritdoc/>
    public override FeatureSet? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        try
        {
            return FeatureFiles.FromBytes(reader.GetBytesFromBase64());
        }
        catch (InvalidDataException e)
        {
            throw new JsonException($"The features can't be read: {e.Message}", e);
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, FeatureSet value, JsonSerializerOptions options) => writer.WriteBase64StringValue(FeatureFiles.ToBytes(value));
}
