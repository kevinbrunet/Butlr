using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Butlr.VDevice.Config;

public sealed class VDeviceYamlSerializer
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public string Serialize<T>(T obj) => Serializer.Serialize(obj);

    public T Deserialize<T>(string yaml) => Deserializer.Deserialize<T>(yaml);

    public T DeserializeFile<T>(string path) => Deserialize<T>(File.ReadAllText(path));
}
