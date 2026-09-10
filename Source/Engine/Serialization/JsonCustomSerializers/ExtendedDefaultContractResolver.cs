// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace FlaxEngine.Json.JsonCustomSerializers
{
    internal class ExtendedDefaultContractResolver : DefaultContractResolver
    {
        private readonly Type _flaxType = typeof(Object);

        private readonly Type[] AttributesIgnoreList =
        {
            typeof(NonSerializedAttribute),
            typeof(NoSerializeAttribute)
        };

        private readonly Type[] AttributesIgnoreListManaged =
        {
            typeof(UnmanagedAttribute),
            typeof(NonSerializedAttribute),
            typeof(NoSerializeAttribute)
        };

        private readonly Type[] _attributesIgnoreList;

        public ExtendedDefaultContractResolver(bool isManagedOnly)
        {
            _attributesIgnoreList = isManagedOnly ? AttributesIgnoreListManaged : AttributesIgnoreList;
        }

        /// <inheritdoc />
        protected override JsonContract CreateContract(Type objectType)
        {
            var contract = base.CreateContract(objectType);

            // Override contract for Flax objects
            if (_flaxType.IsAssignableFrom(objectType))
            {
                ((JsonObjectContract)contract).ItemReferenceLoopHandling = ReferenceLoopHandling.Serialize;
            }

            // Check if use enum serialization as string
            var type = Nullable.GetUnderlyingType(objectType) ?? objectType;
            if (type.IsEnum && type.GetCustomAttribute<EnumStringAttribute>() != null)
            {
                contract.Converter = new StringEnumConverter();
            }

            return contract;
        }

        /// <inheritdoc />
        protected override JsonDictionaryContract CreateDictionaryContract(Type objectType)
        {
            var contract = base.CreateDictionaryContract(objectType);

            // Override contract to save enums keys as integer
            var keyType = contract.DictionaryKeyType;
            if ((keyType?.IsEnum ?? false) && keyType.GetCustomAttribute<EnumStringAttribute>() == null)
            {
                contract.DictionaryKeyResolver = name =>
                {
                    try
                    {
                        var e = Enum.Parse(keyType, name);
                        name = Convert.ToInt32(e).ToString();
                    }
                    catch (Exception ex)
                    {
                        Debug.Logger.LogHandler.LogWrite(LogType.Warning, $"Failed to parse enum '{name}' as {keyType.Name}: {ex.Message}");
                    }
                    return name;
                };
            }

            return contract;
        }

        /// <summary>
        /// Detects an F# record type without taking a compile-time dependency on FSharp.Core,
        /// which is absent from projects that contain no F#.
        ///
        /// The compiler stamps every record with CompilationMappingAttribute carrying
        /// SourceConstructFlags.RecordType (2). That is the only reliable signal: a record's
        /// backing fields are private and its properties are get-only, which is otherwise
        /// indistinguishable from an ordinary immutable C# class.
        /// </summary>
        private static bool IsFSharpRecord(Type type)
        {
            if (type == null || type.IsPrimitive || type == typeof(string) || !type.IsClass && !type.IsValueType)
                return false;
            foreach (var attribute in type.GetCustomAttributes(false))
            {
                var attributeType = attribute.GetType();
                if (attributeType.FullName != "Microsoft.FSharp.Core.CompilationMappingAttribute")
                    continue;
                var flags = attributeType.GetProperty("SourceConstructFlags")?.GetValue(attribute);
                if (flags != null && (int)flags == 2 /* SourceConstructFlags.RecordType */)
                    return true;
            }
            return false;
        }

        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            // An F# record has private backing fields and get-only properties, so the rules below
            // would skip every member and emit {} - a silent, total loss of the object's contents.
            // Records are read back through their constructor instead of through Populate.
            var isFSharpRecord = IsFSharpRecord(type);

            var result = new List<JsonProperty>(fields.Length + properties.Length);

            for (int i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                var attributes = f.GetCustomAttributes();

                // Serialize non-public fields only with a proper attribute
                if (!f.IsPublic && !attributes.Any(x => x is SerializeAttribute))
                    continue;

                // Check if has attribute to skip serialization
                bool noSerialize = false;
                foreach (var attribute in attributes)
                {
                    if (_attributesIgnoreList.Contains(attribute.GetType()))
                    {
                        noSerialize = true;
                        break;
                    }
                }

                if (noSerialize)
                    continue;

                var jsonProperty = CreateProperty(f, memberSerialization);
                jsonProperty.Writable = true;
                jsonProperty.Readable = true;

                if (_flaxType.IsAssignableFrom(f.FieldType))
                {
                    jsonProperty.ReferenceLoopHandling = ReferenceLoopHandling.Serialize;
                    jsonProperty.Converter = JsonSerializer.ObjectConverter;
                }

                result.Add(jsonProperty);
            }

            for (int i = 0; i < properties.Length; i++)
            {
                var p = properties[i];

                // Serialize only properties with read/write. A record's properties are get-only by
                // construction, so the write requirement is waived for them and the value is
                // restored through the constructor.
                var recordProperty = isFSharpRecord && p.CanRead && !p.CanWrite;
                if (!recordProperty && !(p.CanRead && p.CanWrite && p.GetIndexParameters().GetLength(0) == 0))
                    continue;
                if (recordProperty && p.GetIndexParameters().GetLength(0) != 0)
                    continue;

                var attributes = p.GetCustomAttributes();

                // Serialize non-public properties only with a proper attribute
                if ((!p.GetMethod.IsPublic || (!recordProperty && !p.SetMethod.IsPublic)) && !attributes.Any(x => x is SerializeAttribute))
                    continue;

                // Check if has attribute to skip serialization
                bool noSerialize = false;
                foreach (var attribute in attributes)
                {
                    if (_attributesIgnoreList.Contains(attribute.GetType()))
                    {
                        noSerialize = true;
                        break;
                    }
                }

                if (noSerialize)
                    continue;

                var isObsolete = attributes.Any(x => x is ObsoleteAttribute);

                var jsonProperty = CreateProperty(p, memberSerialization);
                jsonProperty.Writable = !recordProperty;
                jsonProperty.Readable = !isObsolete;

                // A record-typed member cannot be filled in place, so replace it wholesale rather
                // than letting Populate try (and silently leave the old value behind).
                if (IsFSharpRecord(p.PropertyType))
                    jsonProperty.ObjectCreationHandling = ObjectCreationHandling.Replace;

                if (_flaxType.IsAssignableFrom(p.PropertyType))
                {
                    jsonProperty.ReferenceLoopHandling = ReferenceLoopHandling.Serialize;
                    jsonProperty.Converter = JsonSerializer.ObjectConverter;
                }

                result.Add(jsonProperty);
            }

            return result;
        }
    }
}
