using System.Text.Json;
using System.Text.Json.Serialization;
using ParagliderToolbox.Airflow;

namespace ParagliderToolbox.Modules.Airflow;

/// <summary>Keeps an <see cref="AirflowField"/> in a project file as the airflow file's bytes (base64): compressed and quantized.</summary>
public sealed class AirflowFieldJsonConverter : JsonConverter<AirflowField>
{
    /// <inheritdoc/>
    public override AirflowField? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        try
        {
            return AirflowField.FromBytes(reader.GetBytesFromBase64());
        }
        catch (InvalidDataException e)
        {
            throw new JsonException($"The airflow result can't be read: {e.Message}", e);
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, AirflowField value, JsonSerializerOptions options) => writer.WriteBase64StringValue(value.ToBytes());
}
