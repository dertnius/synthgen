using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SynthGen.Core.Support;

/// <summary>The one YAML shape every loader in the repo agrees on.</summary>
public static class Yaml
{
    public static IDeserializer Deserializer() => new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
}
