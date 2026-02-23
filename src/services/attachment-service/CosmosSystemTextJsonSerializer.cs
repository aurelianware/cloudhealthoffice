using Microsoft.Azure.Cosmos;
using Azure.Core.Serialization;
using System.Text.Json;

namespace AttachmentService;

public class CosmosSystemTextJsonSerializer : CosmosSerializer
{
    private readonly JsonObjectSerializer _systemTextJsonSerializer;

    public CosmosSystemTextJsonSerializer(JsonSerializerOptions jsonSerializerOptions)
    {
        _systemTextJsonSerializer = new JsonObjectSerializer(jsonSerializerOptions);
    }

    public override T FromStream<T>(Stream stream)
    {
        using (stream)
        {
            if (stream.CanSeek && stream.Length == 0)
            {
                return default!;
            }

            if (typeof(Stream).IsAssignableFrom(typeof(T)))
            {
                return (T)(object)stream;
            }

            return (T)_systemTextJsonSerializer.Deserialize(stream, typeof(T), default)!;
        }
    }

    public override Stream ToStream<T>(T input)
    {
        var stream = new MemoryStream();
        _systemTextJsonSerializer.Serialize(stream, input, typeof(T), default);
        stream.Position = 0;
        return stream;
    }
}
