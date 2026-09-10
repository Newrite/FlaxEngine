// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections;
using System.ComponentModel;
using FlaxEditor.Scripting;

namespace FlaxEditor.CustomEditors
{
    /// <summary>
    /// Values container for a field of an F# record. Record fields cannot be assigned, so setting one replaces each parent record with an updated copy; the editor then writes the parent value up the chain the same way it does for structures.
    /// </summary>
    /// <seealso cref="FlaxEditor.CustomEditors.ValueContainer" />
    public sealed class FSharpRecordValueContainer : ValueContainer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FSharpRecordValueContainer"/> class.
        /// </summary>
        /// <param name="info">The record field.</param>
        /// <param name="instanceValues">The parent (record) values.</param>
        public FSharpRecordValueContainer(ScriptMemberInfo info, ValueContainer instanceValues)
        : base(info, instanceValues)
        {
        }

        /// <inheritdoc />
        public override void Set(ValueContainer instanceValues, object value)
        {
            if (instanceValues == null)
                throw new ArgumentNullException();
            if (instanceValues.Count != Count)
                throw new ArgumentException();

            var perInstance = value is IList l && l.Count == Count && Count > 1 ? l : null;
            for (int i = 0; i < Count; i++)
                Replace(instanceValues, i, perInstance != null ? perInstance[i] : value);
        }

        /// <inheritdoc />
        public override void Set(ValueContainer instanceValues, ValueContainer values)
        {
            if (instanceValues == null || values == null)
                throw new ArgumentNullException();
            if (instanceValues.Count != Count || values.Count != Count)
                throw new ArgumentException();

            for (int i = 0; i < Count; i++)
                Replace(instanceValues, i, values[i]);
        }

        /// <inheritdoc />
        public override void Set(ValueContainer instanceValues)
        {
            if (instanceValues == null)
                throw new ArgumentNullException();
            if (instanceValues.Count != Count)
                throw new ArgumentException();

            for (int i = 0; i < Count; i++)
                Replace(instanceValues, i, this[i]);
        }

        private void Replace(ValueContainer instanceValues, int index, object value)
        {
            // Same automatic conversion as ScriptMemberInfo.SetValue applies for settable members
            var type = Info.ValueType.Type;
            if (value != null && type != null && !type.IsInstanceOfType(value))
            {
                var converter = TypeDescriptor.GetConverter(type);
                if (converter.CanConvertFrom(value.GetType()))
                    value = converter.ConvertFrom(null, null, value);
            }

            instanceValues[index] = FSharpRecord.With(instanceValues[index], Info.Name, value);
            this[index] = Info.GetValue(instanceValues[index]);
        }
    }
}
