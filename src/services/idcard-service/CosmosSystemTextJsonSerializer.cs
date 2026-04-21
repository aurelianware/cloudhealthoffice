using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace IdCardService;

/// <summary>
/// Cosmos serializer that delegates to System.Text.Json — mirrors the
/// eligibility-service implementation so JSON conventions are identical.
/// </summary>
public class CosmosSystemTextJsonSerializer : CosmosSerializer
{
    private readonly JsonSerializerOptions _options;

    public CosmosSystemTextJsonSerializer(JsonSerializerOptions options)
    {
        _options = options;
    }

    public override T FromStream<T>(Stream stream)
    {
        using (stream)
        {
            if (typeof(Stream).IsAssignableFrom(typeof(T)))
            {
                return (T)(object)stream;
            }
            return JsonSerializer.Deserialize<T>(stream, _options)!;
        }
    }

    public override Stream ToStream<T>(T input)
    {
        var ms = new MemoryStream();
        JsonSerializer.Serialize(ms, input, _options);
        ms.Position = 0;
        return ms;
    }
}
