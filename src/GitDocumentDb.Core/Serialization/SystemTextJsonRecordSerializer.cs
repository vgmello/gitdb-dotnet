using System.Buffers;
using System.Text.Json;

namespace GitDocumentDb.Serialization;

public sealed class SystemTextJsonRecordSerializer : IRecordSerializer
{
    private static readonly JsonSerializerOptions s_options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public string FileExtension => ".json";

    public void Serialize<T>(T record, IBufferWriter<byte> output)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { SkipValidation = true });
        JsonSerializer.Serialize(writer, record, s_options);
    }

    public T Deserialize<T>(ReadOnlySpan<byte> input)
    {
        var reader = new Utf8JsonReader(input);
        return JsonSerializer.Deserialize<T>(ref reader, s_options)
            ?? throw new InvalidOperationException("Record deserialized to null");
    }
}
