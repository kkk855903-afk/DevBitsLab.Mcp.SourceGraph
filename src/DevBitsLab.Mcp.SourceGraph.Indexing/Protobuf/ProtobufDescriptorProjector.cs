using DevBitsLab.Mcp.SourceGraph.Sdk;
using Google.Protobuf.Reflection;

namespace DevBitsLab.Mcp.SourceGraph.Indexing.Protobuf;

internal static class ProtobufDescriptorProjector
{
    public static List<IndexEvent> Project(FileDescriptorProto descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var projector = new Projector(descriptor);
        return projector.Run();
    }

    private sealed class Projector
    {
        private readonly FileDescriptorProto _file;
        private readonly SourceLocationIndex _locations;
        private readonly Dictionary<string, DescriptorProto> _messageTypes =
            new(StringComparer.Ordinal);
        private readonly List<IndexEvent> _events = [];
        private int _declarationCount;

        public Projector(FileDescriptorProto file)
        {
            _file = file;
            _locations = new SourceLocationIndex(file.SourceCodeInfo);
            IndexMessages(
                file.MessageType,
                string.IsNullOrEmpty(file.Package)
                    ? null
                    : file.Package);
        }

        public List<IndexEvent> Run()
        {
            for (var index = 0; index < _file.MessageType.Count; index++)
            {
                ProjectMessage(
                    _file.MessageType[index],
                    Qualify(_file.Package, _file.MessageType[index].Name),
                    parentFullName: null,
                    nestingDepth: 0,
                    [4, index]);
            }

            for (var serviceIndex = 0;
                 serviceIndex < _file.Service.Count;
                 serviceIndex++)
            {
                ProjectService(
                    _file.Service[serviceIndex],
                    serviceIndex);
            }
            return _events;
        }

        private void IndexMessages(
            IEnumerable<DescriptorProto> messages,
            string? parentFullName)
        {
            foreach (var message in messages)
            {
                var fullName = Qualify(parentFullName, message.Name);
                _messageTypes.Add(fullName, message);
                IndexMessages(message.NestedType, fullName);
            }
        }

        private void ProjectMessage(
            DescriptorProto message,
            string fullName,
            string? parentFullName,
            int nestingDepth,
            IReadOnlyList<int> path)
        {
            if (nestingDepth
                > ProtobufLanguageIndexer.MaximumMessageNesting)
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.LimitExceeded,
                    "The protobuf descriptor exceeds the "
                    + $"{ProtobufLanguageIndexer.MaximumMessageNesting}-level "
                    + "message nesting limit.");
            }

            // protoc represents a map as a synthetic nested map-entry message. The source field
            // is the contract surface; emitting the synthetic implementation type would invent a
            // declaration that does not exist in the .proto file.
            if (message.Options?.MapEntry == true) return;

            CountDeclaration();
            var key = ProtoCanonicalKeys.ForMessage(fullName);
            var declaration = _locations.Require(path);
            var name = _locations.TryGet(Append(path, 1))
                ?? declaration;
            _events.Add(new IndexEvent.SymbolDeclared(
                key,
                message.Name,
                fullName,
                SymbolKinds.Message,
                name.StartLine,
                name.StartColumn,
                declaration.EndLine,
                declaration.EndColumn,
                signature: $"message {fullName}",
                containerCanonicalKey: parentFullName is null
                    ? null
                    : ProtoCanonicalKeys.ForMessage(parentFullName),
                accessibility: 6));
            _events.Add(CreateAnnotation(
                key,
                "ProtoMessageContract",
                "protobuf.contract.v1.message",
                new ProtoContractFact(
                    ProtoContractKind.Message,
                    key,
                    _file.Package,
                    fullName,
                    ProtoContractStatus.Complete,
                    Array.Empty<string>(),
                    _file.Dependency.Count,
                    new ProtoMessageContract(
                        parentFullName,
                        nestingDepth),
                    null,
                    null)));

            for (var fieldIndex = 0;
                 fieldIndex < message.Field.Count;
                 fieldIndex++)
            {
                ProjectField(
                    message,
                    message.Field[fieldIndex],
                    fullName,
                    Append(path, 2, fieldIndex));
            }
            for (var nestedIndex = 0;
                 nestedIndex < message.NestedType.Count;
                 nestedIndex++)
            {
                var nested = message.NestedType[nestedIndex];
                ProjectMessage(
                    nested,
                    Qualify(fullName, nested.Name),
                    fullName,
                    nestingDepth + 1,
                    Append(path, 3, nestedIndex));
            }
        }

        private void ProjectField(
            DescriptorProto containingMessage,
            FieldDescriptorProto field,
            string containingFullName,
            IReadOnlyList<int> path)
        {
            CountDeclaration();
            var key = ProtoCanonicalKeys.ForField(
                containingFullName,
                field.Name);
            var fullName = Qualify(containingFullName, field.Name);
            var type = RenderFieldType(field);
            var cardinality = GetCardinality(field);
            var oneofName = GetOneofName(containingMessage, field);
            var declaration = _locations.Require(path);
            var name = _locations.TryGet(Append(path, 1))
                ?? declaration;
            _events.Add(new IndexEvent.SymbolDeclared(
                key,
                field.Name,
                fullName,
                SymbolKinds.ProtoField,
                name.StartLine,
                name.StartColumn,
                declaration.EndLine,
                declaration.EndColumn,
                signature: FieldSignature(
                    field,
                    type,
                    cardinality),
                containerCanonicalKey:
                    ProtoCanonicalKeys.ForMessage(containingFullName),
                modifiers: FieldModifiers(cardinality, oneofName),
                accessibility: 6));
            _events.Add(CreateAnnotation(
                key,
                "ProtoFieldContract",
                "protobuf.contract.v1.field",
                new ProtoContractFact(
                    ProtoContractKind.Field,
                    key,
                    _file.Package,
                    fullName,
                    ProtoContractStatus.Complete,
                    Array.Empty<string>(),
                    _file.Dependency.Count,
                    null,
                    new ProtoFieldContract(
                        containingFullName,
                        type,
                        field.Number,
                        cardinality,
                        oneofName),
                    null)));
        }

        private void ProjectService(
            ServiceDescriptorProto service,
            int serviceIndex)
        {
            var serviceFullName = Qualify(
                _file.Package,
                service.Name);
            for (var methodIndex = 0;
                 methodIndex < service.Method.Count;
                 methodIndex++)
            {
                var method = service.Method[methodIndex];
                var path = new[] { 6, serviceIndex, 2, methodIndex };
                CountDeclaration();
                var key = ProtoCanonicalKeys.ForRpc(
                    serviceFullName,
                    method.Name);
                var fullName = Qualify(serviceFullName, method.Name);
                var declaration = _locations.Require(path);
                var name = _locations.TryGet(Append(path, 1))
                    ?? declaration;
                var inputType = NormalizeTypeName(method.InputType);
                var outputType = NormalizeTypeName(method.OutputType);
                _events.Add(new IndexEvent.SymbolDeclared(
                    key,
                    method.Name,
                    fullName,
                    SymbolKinds.Rpc,
                    name.StartLine,
                    name.StartColumn,
                    declaration.EndLine,
                    declaration.EndColumn,
                    signature: RpcSignature(
                        method,
                        inputType,
                        outputType),
                    accessibility: 6));
                _events.Add(CreateAnnotation(
                    key,
                    "ProtoRpcContract",
                    "protobuf.contract.v1.rpc",
                    new ProtoContractFact(
                        ProtoContractKind.Rpc,
                        key,
                        _file.Package,
                        fullName,
                        ProtoContractStatus.Complete,
                        Array.Empty<string>(),
                        _file.Dependency.Count,
                        null,
                        null,
                        new ProtoRpcContract(
                            serviceFullName,
                            inputType,
                            outputType,
                            method.ClientStreaming,
                            method.ServerStreaming))));
            }
        }

        private string RenderFieldType(FieldDescriptorProto field)
        {
            if (field.Type == FieldDescriptorProto.Types.Type.Message)
            {
                var normalized = NormalizeTypeName(field.TypeName);
                if (_messageTypes.TryGetValue(
                        normalized,
                        out var message)
                    && message.Options?.MapEntry == true
                    && message.Field.Count == 2)
                {
                    return "map<"
                        + RenderFieldType(message.Field[0])
                        + ","
                        + RenderFieldType(message.Field[1])
                        + ">";
                }
                return normalized;
            }
            if (field.Type == FieldDescriptorProto.Types.Type.Enum)
            {
                return NormalizeTypeName(field.TypeName);
            }
            return field.Type switch
            {
                FieldDescriptorProto.Types.Type.Double => "double",
                FieldDescriptorProto.Types.Type.Float => "float",
                FieldDescriptorProto.Types.Type.Int64 => "int64",
                FieldDescriptorProto.Types.Type.Uint64 => "uint64",
                FieldDescriptorProto.Types.Type.Int32 => "int32",
                FieldDescriptorProto.Types.Type.Fixed64 => "fixed64",
                FieldDescriptorProto.Types.Type.Fixed32 => "fixed32",
                FieldDescriptorProto.Types.Type.Bool => "bool",
                FieldDescriptorProto.Types.Type.String => "string",
                FieldDescriptorProto.Types.Type.Group => "group",
                FieldDescriptorProto.Types.Type.Bytes => "bytes",
                FieldDescriptorProto.Types.Type.Uint32 => "uint32",
                FieldDescriptorProto.Types.Type.Sfixed32 => "sfixed32",
                FieldDescriptorProto.Types.Type.Sfixed64 => "sfixed64",
                FieldDescriptorProto.Types.Type.Sint32 => "sint32",
                FieldDescriptorProto.Types.Type.Sint64 => "sint64",
                _ => throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.InvalidDescriptorSet,
                    $"Unsupported protobuf field type `{field.Type}`."),
            };
        }

        private static ProtoFieldCardinality GetCardinality(
            FieldDescriptorProto field)
        {
            if (field.Label
                == FieldDescriptorProto.Types.Label.Repeated)
            {
                return ProtoFieldCardinality.Repeated;
            }
            if (field.Label
                == FieldDescriptorProto.Types.Label.Required)
            {
                return ProtoFieldCardinality.Required;
            }
            if (field.Proto3Optional)
            {
                return ProtoFieldCardinality.Optional;
            }
            return ProtoFieldCardinality.Singular;
        }

        private static string? GetOneofName(
            DescriptorProto message,
            FieldDescriptorProto field)
        {
            if (!field.HasOneofIndex || field.Proto3Optional)
            {
                return null;
            }
            if (field.OneofIndex < 0
                || field.OneofIndex >= message.OneofDecl.Count)
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.InvalidDescriptorSet,
                    "A protobuf field has an invalid oneof index.");
            }
            return message.OneofDecl[field.OneofIndex].Name;
        }

        private void CountDeclaration()
        {
            _declarationCount++;
            if (_declarationCount
                > ProtobufLanguageIndexer.MaximumDeclarations)
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.LimitExceeded,
                    "The protobuf descriptor exceeds the "
                    + $"{ProtobufLanguageIndexer.MaximumDeclarations}-declaration limit.");
            }
        }

        private static IndexEvent.AnnotationAttached CreateAnnotation(
            string key,
            string annotationName,
            string fullName,
            ProtoContractFact fact) =>
            new(
                key,
                annotationName,
                ProtoContractAnnotations.Flavor,
                fullName,
                ProtoContractPayloadCodec.Encode(fact));

        private static string FieldSignature(
            FieldDescriptorProto field,
            string type,
            ProtoFieldCardinality cardinality)
        {
            var prefix = cardinality switch
            {
                ProtoFieldCardinality.Optional => "optional ",
                ProtoFieldCardinality.Repeated => "repeated ",
                ProtoFieldCardinality.Required => "required ",
                _ => string.Empty,
            };
            return $"{prefix}{type} {field.Name} = {field.Number}";
        }

        private static string? FieldModifiers(
            ProtoFieldCardinality cardinality,
            string? oneofName)
        {
            var values = new List<string>(2);
            if (cardinality != ProtoFieldCardinality.Singular)
            {
                values.Add(cardinality
                    .ToString()
                    .ToLowerInvariant());
            }
            if (oneofName is not null)
            {
                values.Add("oneof:" + oneofName);
            }
            return values.Count == 0
                ? null
                : string.Join(' ', values);
        }

        private static string RpcSignature(
            MethodDescriptorProto method,
            string inputType,
            string outputType)
        {
            var input = method.ClientStreaming
                ? "stream " + inputType
                : inputType;
            var output = method.ServerStreaming
                ? "stream " + outputType
                : outputType;
            return $"rpc {method.Name}({input}) returns ({output})";
        }

        private static string NormalizeTypeName(string value) =>
            value.StartsWith(".", StringComparison.Ordinal)
                ? value[1..]
                : value;

        private static string Qualify(
            string? parent,
            string name) =>
            string.IsNullOrEmpty(parent)
                ? name
                : parent + "." + name;

        private static int[] Append(
            IReadOnlyList<int> path,
            params int[] suffix)
        {
            var result = new int[path.Count + suffix.Length];
            for (var index = 0; index < path.Count; index++)
            {
                result[index] = path[index];
            }
            suffix.CopyTo(result, path.Count);
            return result;
        }
    }

    private sealed class SourceLocationIndex
    {
        private readonly Dictionary<string, SourceSpan> _locations =
            new(StringComparer.Ordinal);

        public SourceLocationIndex(SourceCodeInfo? sourceInfo)
        {
            if (sourceInfo is null)
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.InvalidDescriptorSet,
                    "The protobuf descriptor is missing source information.");
            }
            foreach (var location in sourceInfo.Location)
            {
                if (location.Path.Count == 0) continue;
                var key = Key(location.Path);
                if (!_locations.TryAdd(
                        key,
                        ParseSpan(location.Span)))
                {
                    throw ProtobufLanguageIndexer.Failure(
                        ProtobufSourceFailureKind.InvalidDescriptorSet,
                        "The protobuf descriptor contains duplicate source paths.");
                }
            }
        }

        public SourceSpan Require(IReadOnlyList<int> path) =>
            TryGet(path)
            ?? throw ProtobufLanguageIndexer.Failure(
                ProtobufSourceFailureKind.InvalidDescriptorSet,
                "The protobuf descriptor is missing declaration source evidence.");

        public SourceSpan? TryGet(IReadOnlyList<int> path) =>
            _locations.TryGetValue(Key(path), out var span)
                ? span
                : null;

        private static SourceSpan ParseSpan(
            IReadOnlyList<int> values)
        {
            if (values.Count is not (3 or 4)
                || values.Any(value => value < 0))
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.InvalidDescriptorSet,
                    "The protobuf descriptor contains an invalid source span.");
            }
            var startLine = values[0] + 1;
            var startColumn = values[1] + 1;
            var endLine = values.Count == 3
                ? startLine
                : values[2] + 1;
            var endColumn = values.Count == 3
                ? values[2] + 1
                : values[3] + 1;
            if (endLine < startLine
                || (endLine == startLine
                    && endColumn < startColumn))
            {
                throw ProtobufLanguageIndexer.Failure(
                    ProtobufSourceFailureKind.InvalidDescriptorSet,
                    "The protobuf descriptor contains a reversed source span.");
            }
            return new SourceSpan(
                startLine,
                startColumn,
                endLine,
                Math.Max(1, endColumn));
        }

        private static string Key(IEnumerable<int> values) =>
            string.Join(',', values);
    }

    private sealed record SourceSpan(
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn);
}
