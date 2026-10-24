using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BrokerForge.Descriptors.Parsing;

/// <summary>
/// Parses descriptor YAML. Unknown keys anywhere are an error, not ignored (descriptor spec 1),
/// because the deserializer is built without IgnoreUnmatchedProperties.
/// This is the only file that depends on YamlDotNet.
/// </summary>
public static class DescriptorParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new ValueBindingConverter())
        .WithTypeConverter(new ColumnMappingConverter())
        .WithTypeConverter(new ErrorMappingConverter())
        .Build();

    public static OperationDescriptor Parse(string yaml, string source)
    {
        try
        {
            return Deserializer.Deserialize<OperationDescriptor>(yaml)
                ?? throw new BrokerConfigurationException($"Descriptor {source} is empty.");
        }
        catch (YamlException ex)
        {
            var reason = ex.InnerException?.Message ?? ex.Message;
            throw new BrokerConfigurationException($"Descriptor {source} line {ex.Start.Line}: {reason}", ex);
        }
    }

    public static OperationDescriptor ParseFile(string path) => Parse(File.ReadAllText(path), path);

    private sealed class ValueBindingConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ValueBinding);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                return ValueBinding.FromLiteral(scalar.Value);
            }

            var dto = (BindingDto)rootDeserializer(typeof(BindingDto))!;
            return new ValueBinding
            {
                Static = dto.Static,
                Instance = dto.Instance,
                Secret = dto.Secret,
                Context = dto.Context,
                Col = dto.Col,
                Id = dto.Id,
                Filter = dto.Filter,
                Sort = dto.Sort,
                Param = dto.Param,
                Computed = dto.Computed,
                Step = dto.Step,
                Ops = dto.Ops,
                Format = dto.Format,
                Required = dto.Required,
                OmitIfNull = dto.OmitIfNull,
                AlwaysSend = dto.AlwaysSend,
            };
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            throw new NotSupportedException("Descriptors are authored as YAML; writing is SwaggerForge's job.");
    }

    private sealed class ColumnMappingConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ColumnMapping);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                return new ColumnMapping { Path = scalar.Value };
            }

            var dto = (ColumnMappingDto)rootDeserializer(typeof(ColumnMappingDto))!;
            return new ColumnMapping { Path = dto.Path, Type = dto.Type, Jsonata = dto.Jsonata };
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            throw new NotSupportedException();
    }

    private sealed class ErrorMappingConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ErrorMapping);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            if (parser.TryConsume<Scalar>(out var scalar))
            {
                return new ErrorMapping { Exception = scalar.Value };
            }

            var dto = (ErrorMappingDto)rootDeserializer(typeof(ErrorMappingDto))!;
            return new ErrorMapping { Exception = dto.Exception ?? "", Message = dto.Message };
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            throw new NotSupportedException();
    }

    private sealed class BindingDto
    {
        public string? Static { get; set; }
        public string? Instance { get; set; }
        public string? Secret { get; set; }
        public string? Context { get; set; }
        public string? Col { get; set; }
        public string? Id { get; set; }
        public string? Filter { get; set; }
        public Dictionary<string, string>? Sort { get; set; }
        public string? Param { get; set; }
        public string? Computed { get; set; }
        public string? Step { get; set; }
        public List<string>? Ops { get; set; }
        public string? Format { get; set; }
        public bool? Required { get; set; }
        public bool? OmitIfNull { get; set; }
        public bool? AlwaysSend { get; set; }
    }

    private sealed class ColumnMappingDto
    {
        public string? Path { get; set; }
        public string? Type { get; set; }
        public string? Jsonata { get; set; }
    }

    private sealed class ErrorMappingDto
    {
        public string? Exception { get; set; }
        public string? Message { get; set; }
    }
}
