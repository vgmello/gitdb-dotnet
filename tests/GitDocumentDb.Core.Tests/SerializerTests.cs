using System.Buffers;
using FluentAssertions;
using GitDocumentDb.Serialization;

namespace GitDocumentDb.Tests;

public class SerializerTests
{
    public sealed record TestRecord(string Id, int Count, string? Note);

    [Fact]
    public void Roundtrip_preserves_record()
    {
        var s = new SystemTextJsonRecordSerializer();
        var writer = new ArrayBufferWriter<byte>();
        s.Serialize(new TestRecord("x", 5, "n"), writer);
        var result = s.Deserialize<TestRecord>(writer.WrittenSpan);
        result.Should().Be(new TestRecord("x", 5, "n"));
    }

    [Fact]
    public void Extension_is_json()
    {
        new SystemTextJsonRecordSerializer().FileExtension.Should().Be(".json");
    }
}
