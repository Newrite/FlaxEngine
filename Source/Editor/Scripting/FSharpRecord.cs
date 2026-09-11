// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.FSharp.Core;
using Microsoft.FSharp.Reflection;

namespace FlaxEditor.Scripting
{
    /// <summary>
    /// Helpers for F# record types. Records are immutable - their fields are get-only properties set through the constructor - so editing a field means building an updated copy of the record.
    /// </summary>
    /// <remarks>
    /// Built on FSharp.Core's own reflection API (Microsoft.FSharp.Reflection). This is editor code, so the FSharp.Core reference stays out of the engine assembly games ship.
    /// </remarks>
    public static class FSharpRecord
    {
        // Public and non-public: a record with a private representation is still a record
        private static readonly FSharpOption<BindingFlags> AllRepresentations = FSharpOption<BindingFlags>.Some(BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>
        /// Checks if the given type is an F# record.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>True if the type is an F# record, otherwise false.</returns>
        public static bool IsRecord(Type type)
        {
            return type != null && FSharpType.IsRecord(type, AllRepresentations);
        }

        /// <summary>
        /// Checks if the given member is a field of an F# record.
        /// </summary>
        /// <param name="member">The member.</param>
        /// <returns>True if the member is a record field property, otherwise false.</returns>
        public static bool IsField(MemberInfo member)
        {
            if (!(member is PropertyInfo) || !IsRecord(member.DeclaringType))
                return false;
            return FSharpType.GetRecordFields(member.DeclaringType, AllRepresentations).Any(x => x.Name == member.Name);
        }

        /// <summary>
        /// Gets the fields of an F# record in declaration order, which is the order of the record constructor parameters.
        /// </summary>
        /// <param name="type">The record type.</param>
        /// <returns>The field properties.</returns>
        public static PropertyInfo[] GetFields(Type type)
        {
            if (!IsRecord(type))
                throw new ArgumentException($"Type '{type}' is not an F# record.", nameof(type));
            return FSharpType.GetRecordFields(type, AllRepresentations);
        }

        /// <summary>
        /// Creates an F# record with default field values: empty strings, arrays and F# lists/maps/sets, zero for value types, <c>None</c> for options and default records for nested records - values F# code can use, not nulls.
        /// </summary>
        /// <param name="type">The record type.</param>
        /// <returns>The new record.</returns>
        public static object CreateDefault(Type type)
        {
            return CreateDefault(type, new HashSet<Type>());
        }

        private static object CreateDefault(Type type, HashSet<Type> creating)
        {
            var fields = GetFields(type);
            creating.Add(type);
            try
            {
                return FSharpValue.MakeRecord(type, fields.Select(x => GetDefaultValue(x.PropertyType, creating)).ToArray(), AllRepresentations);
            }
            finally
            {
                creating.Remove(type);
            }
        }

        private static object GetDefaultValue(Type type, HashSet<Type> creating)
        {
            if (type == typeof(string))
                return string.Empty;
            if (type.IsArray)
                return Array.CreateInstance(type.GetElementType(), 0);
            if (type.IsValueType)
                return Activator.CreateInstance(type);
            if (IsRecord(type) && !creating.Contains(type))
                return CreateDefault(type, creating);

            // F# list exposes its empty value as a static Empty property
            var empty = type.GetProperty("Empty", BindingFlags.Public | BindingFlags.Static);
            if (empty != null && type.IsAssignableFrom(empty.PropertyType))
                return empty.GetValue(null);

            // F# Map and Set have no such property but are constructed from a sequence of their elements
            foreach (var constructor in type.GetConstructors())
            {
                var parameters = constructor.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType.IsGenericType && parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    return constructor.Invoke(new object[] { Array.CreateInstance(parameters[0].ParameterType.GetGenericArguments()[0], 0) });
            }

            // Options (None is null), classes, and records that contain themselves
            return null;
        }

        /// <summary>
        /// Builds a copy of an F# record with one field changed (the F# <c>{ record with Field = value }</c>).
        /// </summary>
        /// <param name="record">The record.</param>
        /// <param name="fieldName">The name of the field to change.</param>
        /// <param name="value">The new field value.</param>
        /// <returns>The new record.</returns>
        public static object With(object record, string fieldName, object value)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            var type = record.GetType();
            var index = Array.FindIndex(GetFields(type), x => x.Name == fieldName);
            if (index < 0)
                throw new ArgumentException($"F# record '{type}' has no field '{fieldName}'.", nameof(fieldName));
            var values = FSharpValue.GetRecordFields(record, AllRepresentations);
            values[index] = value;
            return FSharpValue.MakeRecord(type, values, AllRepresentations);
        }
    }
}
