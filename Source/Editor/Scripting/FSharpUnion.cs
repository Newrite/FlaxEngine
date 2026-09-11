// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Microsoft.FSharp.Reflection;

namespace FlaxEditor.Scripting
{
    /// <summary>
    /// Helpers for F# discriminated unions (option and Result included). A union value is immutable, so picking a case or editing a field means building a new value.
    /// </summary>
    /// <remarks>
    /// Built on FSharp.Core's own reflection API. Some unions store a case as null (option's None); that is reported as a real case, not as "unset".
    /// </remarks>
    public static class FSharpUnion
    {
        private static readonly FSharpOption<BindingFlags> AllRepresentations = FSharpOption<BindingFlags>.Some(BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>
        /// Checks if the given type is an F# union edited case by case. F# list is a union too (empty or head and tail) but is edited as a collection, so it is excluded.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>True if the type is an F# union to edit with a case picker, otherwise false.</returns>
        public static bool IsEditableUnion(Type type)
        {
            if (type == null || !FSharpType.IsUnion(type, AllRepresentations))
                return false;
            return !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FSharpList<>));
        }

        /// <summary>
        /// Checks if the given union type stores one of its cases as null (eg. option's None).
        /// </summary>
        /// <param name="type">The union type.</param>
        /// <returns>True if null is a case of the union.</returns>
        public static bool IsNullACase(Type type)
        {
            var representation = type.GetCustomAttribute<CompilationRepresentationAttribute>(false);
            return representation != null && representation.Flags.HasFlag(CompilationRepresentationFlags.UseNullAsTrueValue);
        }

        /// <summary>
        /// Gets the cases of the union type, in declaration order.
        /// </summary>
        /// <param name="type">The union type.</param>
        /// <returns>The cases.</returns>
        public static UnionCaseInfo[] GetCases(Type type)
        {
            return FSharpType.GetUnionCases(type, AllRepresentations);
        }

        /// <summary>
        /// Gets the case of a union value.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="type">The union type (the declared type, not the case subclass).</param>
        /// <returns>The case, or null if the value is null and null is not a case of the union.</returns>
        public static UnionCaseInfo GetCase(object value, Type type)
        {
            if (value == null && !IsNullACase(type))
                return null;
            return FSharpValue.GetUnionFields(value, type, AllRepresentations).Item1;
        }

        /// <summary>
        /// Gets the case name of a union value.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="type">The union type.</param>
        /// <returns>The case name, or null if the value is null and null is not a case of the union.</returns>
        public static string GetCaseName(object value, Type type)
        {
            return GetCase(value, type)?.Name;
        }

        /// <summary>
        /// Creates a value of the given case with default field values (see <see cref="FSharpRecord.CreateDefault"/>).
        /// </summary>
        /// <param name="type">The union type.</param>
        /// <param name="caseName">The case name.</param>
        /// <returns>The new value (null for a case stored as null).</returns>
        public static object CreateCase(Type type, string caseName)
        {
            var unionCase = GetCases(type).FirstOrDefault(x => x.Name == caseName);
            if (unionCase == null)
                throw new ArgumentException($"F# union '{type}' has no case '{caseName}'.", nameof(caseName));
            return MakeCase(unionCase, new HashSet<Type>());
        }

        internal static object CreateDefault(Type type, HashSet<Type> creating)
        {
            // A union that stores a case as null defaults to it (option: None); a recursive one stops at null
            if (IsNullACase(type) || creating.Contains(type))
                return null;
            creating.Add(type);
            try
            {
                return MakeCase(GetCases(type)[0], creating);
            }
            finally
            {
                creating.Remove(type);
            }
        }

        private static object MakeCase(UnionCaseInfo unionCase, HashSet<Type> creating)
        {
            var values = unionCase.GetFields().Select(x => FSharpRecord.GetDefaultValue(x.PropertyType, creating)).ToArray();
            return FSharpValue.MakeUnion(unionCase, values, AllRepresentations);
        }

        /// <summary>
        /// Builds a copy of a union value with one field of its case changed.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="type">The union type.</param>
        /// <param name="fieldName">The name of the case field to change.</param>
        /// <param name="fieldValue">The new field value.</param>
        /// <returns>The new value.</returns>
        public static object WithField(object value, Type type, string fieldName, object fieldValue)
        {
            var unionCase = GetCase(value, type);
            if (unionCase == null)
                throw new ArgumentException($"The value is not a case of F# union '{type}'.", nameof(value));
            var index = Array.FindIndex(unionCase.GetFields(), x => x.Name == fieldName);
            if (index < 0)
                throw new ArgumentException($"Case '{unionCase.Name}' of F# union '{type}' has no field '{fieldName}'.", nameof(fieldName));
            var values = FSharpValue.GetUnionFields(value, type, AllRepresentations).Item2;
            values[index] = fieldValue;
            return FSharpValue.MakeUnion(unionCase, values, AllRepresentations);
        }
    }
}
