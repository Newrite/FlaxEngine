// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FlaxEditor.Scripting;
using FlaxEngine;
using FlaxEngine.Utilities;

namespace FlaxEditor.CustomEditors.Editors
{
    /// <summary>
    /// Default implementation of the inspector used to edit immutable F# collections: a list or a Set is edited like an array and a Map like a dictionary, by the same editors C# collections use, each change stored as a new F# collection.
    /// </summary>
    /// <seealso cref="FlaxEditor.CustomEditors.CustomEditor" />
    public sealed class FSharpCollectionEditor : CustomEditor
    {
        /// <summary>
        /// Array editor for the elements of an F# Set. Growing it adds elements not in the set yet - a copy of an existing element would be dropped as a duplicate and the set could not grow.
        /// </summary>
        private sealed class SetItemsEditor : ArrayEditor
        {
            /// <inheritdoc />
            protected override void Resize(int newSize)
            {
                var array = Values[0] as Array;
                var oldSize = array?.Length ?? 0;
                if (newSize <= oldSize)
                {
                    base.Resize(newSize);
                    return;
                }

                var elementType = Values.Type.GetElementType();
                var newValues = TypeUtils.CreateArrayInstance(elementType, newSize);
                var used = new HashSet<object>();
                for (int i = 0; i < oldSize; i++)
                {
                    var value = array.GetValue(i);
                    newValues.SetValue(value, i);
                    used.Add(value);
                }
                for (int i = oldSize; i < newSize; i++)
                {
                    var value = CreateDistinctValue(elementType, used);
                    newValues.SetValue(value, i);
                    used.Add(value);
                }
                SetValue(newValues);
            }

            private static object CreateDistinctValue(ScriptType elementType, HashSet<object> used)
            {
                var type = elementType.Type;
                if (type != null && type.IsEnum)
                {
                    foreach (var value in Enum.GetValues(type))
                    {
                        if (!used.Contains(value))
                            return value;
                    }
                }
                else if (type == typeof(bool))
                {
                    return used.Contains(false) ? true : false;
                }
                else if (type != null && type.IsPrimitive && type != typeof(char) && type != typeof(IntPtr) && type != typeof(UIntPtr))
                {
                    // Smallest non-negative number not in the set
                    for (long i = 0;; i++)
                    {
                        var value = Convert.ChangeType(i, type);
                        if (!used.Contains(value))
                            return value;
                    }
                }
                else if (type == typeof(string))
                {
                    var value = "Item";
                    for (int i = 1; used.Contains(value); i++)
                        value = "Item " + i;
                    return value;
                }
                return TypeUtils.GetDefaultValue(elementType);
            }
        }

        /// <inheritdoc />
        public override bool RevertValueWithChildren => false; // Always revert a whole collection

        /// <inheritdoc />
        public override void Initialize(LayoutElementsContainer layout)
        {
            // No support for different collections for now
            if (HasDifferentTypes)
                return;

            var type = Values.Type.Type;
            var attributes = Values.GetAttributes();
            CustomEditor itemsEditor;
            if (FSharpCollection.IsMap(type))
            {
                itemsEditor = new DictionaryEditor();
            }
            else if (FSharpCollection.IsSet(type))
            {
                itemsEditor = new SetItemsEditor();

                // A set keeps its elements sorted, so moving them has no effect
                if (attributes == null || !attributes.Any(x => x is CollectionAttribute))
                    attributes = (attributes ?? Array.Empty<object>()).Append(new CollectionAttribute { CanReorderItems = false, Spacing = 1.0f }).ToArray();
            }
            else
            {
                itemsEditor = new ArrayEditor();
            }

            // The items editor shares this layout, so it looks the same as for a C# collection (eg. its size box in the group header)
            layout.Object(new FSharpCollectionValueContainer(Values, attributes), itemsEditor);
        }
    }
}
