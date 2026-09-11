// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FlaxEditor.Scripting
{
    /// <summary>
    /// Helpers for F# record types. Records are immutable - their fields are get-only properties set through the constructor - so editing a field means building an updated copy of the record.
    /// </summary>
    /// <remarks>
    /// Records are recognized by the CompilationMappingAttribute the F# compiler stamps on them (read by name, so the editor does not depend on FSharp.Core).
    /// </remarks>
    public static class FSharpRecord
    {
        private const string CompilationMappingAttribute = "Microsoft.FSharp.Core.CompilationMappingAttribute";
        private const int KindMask = 31; // SourceConstructFlags.KindMask
        private const int RecordType = 2; // SourceConstructFlags.RecordType
        private const int Field = 4; // SourceConstructFlags.Field

        private static bool TryGetMapping(MemberInfo member, out int kind, out int sequenceNumber)
        {
            foreach (var attribute in member.GetCustomAttributes(false))
            {
                var attributeType = attribute.GetType();
                if (attributeType.FullName != CompilationMappingAttribute)
                    continue;
                kind = Convert.ToInt32(attributeType.GetProperty("SourceConstructFlags")?.GetValue(attribute) ?? 0) & KindMask;
                sequenceNumber = Convert.ToInt32(attributeType.GetProperty("SequenceNumber")?.GetValue(attribute) ?? 0);
                return true;
            }
            kind = 0;
            sequenceNumber = 0;
            return false;
        }

        /// <summary>
        /// Checks if the given type is an F# record.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>True if the type is an F# record, otherwise false.</returns>
        public static bool IsRecord(Type type)
        {
            if (type == null || type.IsPrimitive || type == typeof(string))
                return false;
            return TryGetMapping(type, out var kind, out _) && kind == RecordType;
        }

        /// <summary>
        /// Checks if the given member is a field of an F# record.
        /// </summary>
        /// <param name="member">The member.</param>
        /// <returns>True if the member is a record field property, otherwise false.</returns>
        public static bool IsField(MemberInfo member)
        {
            return member is PropertyInfo && IsRecord(member.DeclaringType) && TryGetMapping(member, out var kind, out _) && kind == Field;
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
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                       .Select(p => (Property: p, IsField: TryGetMapping(p, out var kind, out var sequenceNumber) && kind == Field, Order: sequenceNumber))
                       .Where(x => x.IsField)
                       .OrderBy(x => x.Order)
                       .Select(x => x.Property)
                       .ToArray();
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
            var constructor = type.GetConstructor(fields.Select(x => x.PropertyType).ToArray());
            if (constructor == null)
                throw new InvalidOperationException($"F# record '{type}' has no constructor taking all of its fields.");
            creating.Add(type);
            try
            {
                return constructor.Invoke(fields.Select(x => GetDefaultValue(x.PropertyType, creating)).ToArray());
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
            var fields = GetFields(type);
            if (fields.All(x => x.Name != fieldName))
                throw new ArgumentException($"F# record '{type}' has no field '{fieldName}'.", nameof(fieldName));
            var constructor = type.GetConstructor(fields.Select(x => x.PropertyType).ToArray());
            if (constructor == null)
                throw new InvalidOperationException($"F# record '{type}' has no constructor taking all of its fields.");
            var args = fields.Select(x => x.Name == fieldName ? value : x.GetValue(record)).ToArray();
            return constructor.Invoke(args);
        }
    }
}
