// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;

namespace FlaxEngine.Json
{
    /// <summary>
    /// Shared reflection helpers for the F# collection converters.
    ///
    /// Everything here goes through reflection on purpose: the engine must not take a
    /// compile-time dependency on FSharp.Core, which is present only in projects that actually
    /// contain F# code. The FSharp.Core assembly is reached through the type being converted.
    /// </summary>
    internal static class FSharpCollections
    {
        private const string CollectionsNamespace = "Microsoft.FSharp.Collections";

        /// <summary>Matches Microsoft.FSharp.Collections.FSharpMap`2 without referencing it.</summary>
        internal static bool IsMap(Type type)
        {
            return type != null
                   && type.IsGenericType
                   && type.Namespace == CollectionsNamespace
                   && type.Name.StartsWith("FSharpMap", StringComparison.Ordinal);
        }

        /// <summary>Matches Microsoft.FSharp.Collections.FSharpSet`1 without referencing it.</summary>
        internal static bool IsSet(Type type)
        {
            return type != null
                   && type.IsGenericType
                   && type.Namespace == CollectionsNamespace
                   && type.Name.StartsWith("FSharpSet", StringComparison.Ordinal);
        }

        /// <summary>
        /// Finds a module function such as MapModule.OfSeq or SetModule.OfSeq and closes it over
        /// the supplied type arguments.
        /// </summary>
        internal static MethodInfo GetModuleMethod(Type sample, string moduleName, string methodName, params Type[] typeArguments)
        {
            var module = sample.Assembly.GetType(CollectionsNamespace + "." + moduleName);
            if (module == null)
                throw new JsonSerializationException($"Failed to locate {moduleName} in {sample.Assembly.GetName().Name}.");
            var method = module.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                throw new JsonSerializationException($"Failed to locate {moduleName}.{methodName}.");
            return method.MakeGenericMethod(typeArguments);
        }
    }

    /// <summary>
    /// Serializes F# <c>Map&lt;K, V&gt;</c>.
    ///
    /// Without this the map is treated as a generic dictionary and Newtonsoft tries to populate it
    /// through Add, which an immutable map refuses with NotSupportedException. Because
    /// ManagedSerialization catches and logs that exception rather than failing the load, every
    /// member appearing after the map in the document is silently dropped while the scene still
    /// reports as loaded - so this converter prevents data loss, not just an error message.
    ///
    /// A string-keyed map is written as a JSON object, matching how Dictionary&lt;string, T&gt;
    /// looks. Any other key type is written as an array of Key/Value pairs, because JSON property
    /// names are strings and a lossy key conversion would be worse than a slightly noisier shape.
    /// </summary>
    internal class FSharpMapConverter : JsonConverter
    {
        /// <inheritdoc />
        public override bool CanConvert(Type objectType)
        {
            return FSharpCollections.IsMap(objectType);
        }

        /// <inheritdoc />
        public override void WriteJson(JsonWriter writer, object value, Newtonsoft.Json.JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            var arguments = value.GetType().GetGenericArguments();
            var keyType = arguments[0];
            var valueType = arguments[1];
            var asObject = keyType == typeof(string);

            if (asObject)
                writer.WriteStartObject();
            else
                writer.WriteStartArray();

            foreach (var entry in (IEnumerable)value)
            {
                var entryType = entry.GetType();
                var key = entryType.GetProperty("Key").GetValue(entry);
                var item = entryType.GetProperty("Value").GetValue(entry);

                if (asObject)
                {
                    writer.WritePropertyName((string)key);
                    serializer.Serialize(writer, item, valueType);
                }
                else
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("Key");
                    serializer.Serialize(writer, key, keyType);
                    writer.WritePropertyName("Value");
                    serializer.Serialize(writer, item, valueType);
                    writer.WriteEndObject();
                }
            }

            if (asObject)
                writer.WriteEndObject();
            else
                writer.WriteEndArray();
        }

        /// <inheritdoc />
        public override void WriteJsonDiff(JsonWriter writer, object value, object other, Newtonsoft.Json.JsonSerializer serializer)
        {
            // Maps are immutable and compare structurally, so an unchanged map contributes nothing
            // to the diff and a changed one is written whole.
            if (Equals(value, other))
                return;
            WriteJson(writer, value, serializer);
        }

        /// <inheritdoc />
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, Newtonsoft.Json.JsonSerializer serializer)
        {
            var arguments = objectType.GetGenericArguments();
            var keyType = arguments[0];
            var valueType = arguments[1];
            var tupleType = typeof(Tuple<,>).MakeGenericType(keyType, valueType);
            var pairs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(tupleType));

            if (reader.TokenType == JsonToken.Null)
                return null;

            if (reader.TokenType == JsonToken.StartObject)
            {
                while (reader.Read() && reader.TokenType != JsonToken.EndObject)
                {
                    if (reader.TokenType != JsonToken.PropertyName)
                        continue;
                    var key = (string)reader.Value;
                    reader.Read();
                    var item = serializer.Deserialize(reader, valueType);
                    pairs.Add(Activator.CreateInstance(tupleType, key, item));
                }
            }
            else if (reader.TokenType == JsonToken.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                {
                    if (reader.TokenType != JsonToken.StartObject)
                        continue;
                    object key = null, item = null;
                    while (reader.Read() && reader.TokenType != JsonToken.EndObject)
                    {
                        if (reader.TokenType != JsonToken.PropertyName)
                            continue;
                        var name = (string)reader.Value;
                        reader.Read();
                        if (string.Equals(name, "Key", StringComparison.Ordinal))
                            key = serializer.Deserialize(reader, keyType);
                        else if (string.Equals(name, "Value", StringComparison.Ordinal))
                            item = serializer.Deserialize(reader, valueType);
                    }
                    pairs.Add(Activator.CreateInstance(tupleType, key, item));
                }
            }

            var ofSeq = FSharpCollections.GetModuleMethod(objectType, "MapModule", "OfSeq", keyType, valueType);
            return ofSeq.Invoke(null, new object[] { pairs });
        }
    }

    /// <summary>
    /// Serializes F# <c>Set&lt;T&gt;</c> as a JSON array.
    ///
    /// Without this a set member is discarded with no exception and no log line at all - the
    /// quietest of the F# serialization failures, and the hardest to notice in a scene file.
    /// </summary>
    internal class FSharpSetConverter : JsonConverter
    {
        /// <inheritdoc />
        public override bool CanConvert(Type objectType)
        {
            return FSharpCollections.IsSet(objectType);
        }

        /// <inheritdoc />
        public override void WriteJson(JsonWriter writer, object value, Newtonsoft.Json.JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            var elementType = value.GetType().GetGenericArguments()[0];
            writer.WriteStartArray();
            foreach (var item in (IEnumerable)value)
                serializer.Serialize(writer, item, elementType);
            writer.WriteEndArray();
        }

        /// <inheritdoc />
        public override void WriteJsonDiff(JsonWriter writer, object value, object other, Newtonsoft.Json.JsonSerializer serializer)
        {
            if (Equals(value, other))
                return;
            WriteJson(writer, value, serializer);
        }

        /// <inheritdoc />
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, Newtonsoft.Json.JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            var elementType = objectType.GetGenericArguments()[0];
            var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));

            if (reader.TokenType == JsonToken.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                    items.Add(serializer.Deserialize(reader, elementType));
            }

            var ofSeq = FSharpCollections.GetModuleMethod(objectType, "SetModule", "OfSeq", elementType);
            return ofSeq.Invoke(null, new object[] { items });
        }
    }
}
