// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.FSharp.Collections;

namespace FlaxEditor.Scripting
{
    /// <summary>
    /// Helpers for the immutable F# collections: list, Map and Set. They cannot be changed in place, so the editor edits a mutable copy - an array for a list or a set, a <see cref="Dictionary{TKey,TValue}"/> for a map - and builds a new F# collection from it on every change.
    /// </summary>
    /// <remarks>
    /// Built on FSharp.Core's own collection modules. This is editor code, so the FSharp.Core reference stays out of the engine assembly games ship.
    /// </remarks>
    public static class FSharpCollection
    {
        /// <summary>
        /// Checks if the given type is an F# list.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>True if the type is an F# list, otherwise false.</returns>
        public static bool IsList(Type type)
        {
            return IsGeneric(type, typeof(FSharpList<>));
        }

        /// <summary>
        /// Checks if the given type is an F# Map.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>True if the type is an F# Map, otherwise false.</returns>
        public static bool IsMap(Type type)
        {
            return IsGeneric(type, typeof(FSharpMap<,>));
        }

        /// <summary>
        /// Checks if the given type is an F# Set.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>True if the type is an F# Set, otherwise false.</returns>
        public static bool IsSet(Type type)
        {
            return IsGeneric(type, typeof(FSharpSet<>));
        }

        /// <summary>
        /// Checks if the given type is an immutable F# collection: a list, a Map or a Set.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>True if the type is an F# collection, otherwise false.</returns>
        public static bool IsCollection(Type type)
        {
            return IsList(type) || IsMap(type) || IsSet(type);
        }

        /// <summary>
        /// Gets the mutable type an F# collection is edited as: an array of the elements for a list or a set, a <see cref="Dictionary{TKey,TValue}"/> for a map.
        /// </summary>
        /// <param name="type">The F# collection type.</param>
        /// <returns>The editable type.</returns>
        public static Type GetEditableType(Type type)
        {
            if (IsMap(type))
                return typeof(Dictionary<,>).MakeGenericType(type.GetGenericArguments());
            if (IsList(type) || IsSet(type))
                return type.GetGenericArguments()[0].MakeArrayType();
            throw new ArgumentException($"Type '{type}' is not an F# collection.", nameof(type));
        }

        /// <summary>
        /// Copies an F# collection into a new mutable collection of <see cref="GetEditableType"/>, in the collection's order. A null collection is copied as an empty one.
        /// </summary>
        /// <param name="collection">The F# collection (can be null).</param>
        /// <param name="type">The F# collection type.</param>
        /// <returns>The editable copy.</returns>
        public static object ToEditable(object collection, Type type)
        {
            return Call(IsMap(type) ? nameof(MapToDictionary) : nameof(SequenceToArray), type, collection);
        }

        /// <summary>
        /// Builds a new F# collection from its editable copy (see <see cref="ToEditable"/>). A set drops duplicates and sorts its elements, a map sorts its keys. A null copy builds an empty collection.
        /// </summary>
        /// <param name="editable">The editable copy (can be null).</param>
        /// <param name="type">The F# collection type.</param>
        /// <returns>The new F# collection.</returns>
        public static object FromEditable(object editable, Type type)
        {
            if (IsMap(type))
                return Call(nameof(DictionaryToMap), type, editable);
            return Call(IsSet(type) ? nameof(ArrayToSet) : nameof(ArrayToList), type, editable);
        }

        private static bool IsGeneric(Type type, Type genericTypeDefinition)
        {
            return type != null && type.IsGenericType && type.GetGenericTypeDefinition() == genericTypeDefinition;
        }

        private static object Call(string method, Type type, object arg)
        {
            if (!IsCollection(type))
                throw new ArgumentException($"Type '{type}' is not an F# collection.", nameof(type));
            var generic = typeof(FSharpCollection).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static).MakeGenericMethod(type.GetGenericArguments());
            try
            {
                return generic.Invoke(null, new[] { arg });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private static T[] SequenceToArray<T>(object collection)
        {
            return collection is IEnumerable<T> items ? items.ToArray() : Array.Empty<T>();
        }

        private static Dictionary<TKey, TValue> MapToDictionary<TKey, TValue>(object map)
        {
            return map is IDictionary<TKey, TValue> items ? new Dictionary<TKey, TValue>(items) : new Dictionary<TKey, TValue>();
        }

        private static FSharpList<T> ArrayToList<T>(object array)
        {
            return ListModule.OfSeq(Items<T>(array));
        }

        private static FSharpSet<T> ArrayToSet<T>(object array)
        {
            return SetModule.OfSeq(Items<T>(array));
        }

        private static FSharpMap<TKey, TValue> DictionaryToMap<TKey, TValue>(object dictionary)
        {
            return MapModule.OfSeq(Items<KeyValuePair<TKey, TValue>>(dictionary).Select(x => Tuple.Create(x.Key, x.Value)));
        }

        private static IEnumerable<T> Items<T>(object collection)
        {
            // The editors produce arrays and dictionaries, but accept any sequence of the elements (eg. a List<T> from a paste)
            if (collection is IEnumerable<T> items)
                return items;
            if (collection is IEnumerable other)
                return other.Cast<T>();
            return Enumerable.Empty<T>();
        }
    }
}
