using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace BrokerForge.Contracts;

/// <summary>
/// The one serialiser for the canonical contract, used by both plug-in and host so the wire format cannot drift.
/// </summary>
public static class CanonicalJson
{
    private static readonly DataContractJsonSerializerSettings Settings = new DataContractJsonSerializerSettings
    {
        UseSimpleDictionaryFormat = true,
    };

    public static string Serialize<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T), Settings);
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static T Deserialize<T>(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(T), Settings);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)serializer.ReadObject(stream)!;
    }

    public static T Deserialize<T>(Stream stream)
    {
        var serializer = new DataContractJsonSerializer(typeof(T), Settings);
        return (T)serializer.ReadObject(stream)!;
    }
}
