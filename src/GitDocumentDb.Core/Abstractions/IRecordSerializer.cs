using System.Buffers;

namespace GitDocumentDb;

public interface IRecordSerializer
{
    void Serialize<T>(T record, IBufferWriter<byte> output);
    T Deserialize<T>(ReadOnlySpan<byte> input);
    string FileExtension { get; }
}
