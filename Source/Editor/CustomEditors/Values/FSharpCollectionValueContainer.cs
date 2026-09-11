// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using FlaxEditor.Scripting;
using FlaxEngine;

namespace FlaxEditor.CustomEditors
{
    /// <summary>
    /// Values container presenting immutable F# collections (list, Map, Set) as their mutable copies (see <see cref="FSharpCollection.GetEditableType"/>), so the stock collection editors can edit them. Setting a copy replaces each parent collection with a new F# collection built from it; the editor then writes the parent value up the chain the same way it does for structures.
    /// </summary>
    /// <seealso cref="FlaxEditor.CustomEditors.ValueContainer" />
    [HideInEditor]
    public sealed class FSharpCollectionValueContainer : ValueContainer
    {
        private readonly Type _collectionType;
        private readonly object[] _attributes;

        /// <summary>
        /// Initializes a new instance of the <see cref="FSharpCollectionValueContainer"/> class.
        /// </summary>
        /// <param name="collectionValues">The F# collection values.</param>
        /// <param name="attributes">The attributes of the collection member (eg. <see cref="CollectionAttribute"/>).</param>
        public FSharpCollectionValueContainer(ValueContainer collectionValues, object[] attributes)
        : base(ScriptMemberInfo.Null, new ScriptType(FSharpCollection.GetEditableType(collectionValues.Type.Type)))
        {
            _collectionType = collectionValues.Type.Type;
            _attributes = attributes;

            Capacity = collectionValues.Count;
            for (int i = 0; i < collectionValues.Count; i++)
                Add(FSharpCollection.ToEditable(collectionValues[i], _collectionType));

            if (collectionValues.HasDefaultValue)
            {
                _defaultValue = FSharpCollection.ToEditable(collectionValues.DefaultValue, _collectionType);
                _hasDefaultValue = true;
            }
            if (collectionValues.HasReferenceValue)
            {
                _referenceValue = FSharpCollection.ToEditable(collectionValues.ReferenceValue, _collectionType);
                _hasReferenceValue = true;
            }
        }

        /// <inheritdoc />
        public override object[] GetAttributes()
        {
            return _attributes ?? base.GetAttributes();
        }

        /// <inheritdoc />
        public override void Refresh(ValueContainer instanceValues)
        {
            if (instanceValues == null || instanceValues.Count != Count)
                throw new ArgumentException();

            for (int i = 0; i < Count; i++)
                this[i] = FSharpCollection.ToEditable(instanceValues[i], _collectionType);
        }

        /// <inheritdoc />
        public override void Set(ValueContainer instanceValues, object value)
        {
            if (instanceValues == null || instanceValues.Count != Count)
                throw new ArgumentException();

            for (int i = 0; i < Count; i++)
            {
                instanceValues[i] = FSharpCollection.FromEditable(value, _collectionType);
                this[i] = value;
            }
        }

        /// <inheritdoc />
        public override void Set(ValueContainer instanceValues, ValueContainer values)
        {
            if (instanceValues == null || instanceValues.Count != Count)
                throw new ArgumentException();
            if (values == null || values.Count != Count)
                throw new ArgumentException();

            for (int i = 0; i < Count; i++)
            {
                instanceValues[i] = FSharpCollection.FromEditable(values[i], _collectionType);
                this[i] = values[i];
            }
        }

        /// <inheritdoc />
        public override void Set(ValueContainer instanceValues)
        {
            if (instanceValues == null || instanceValues.Count != Count)
                throw new ArgumentException();

            for (int i = 0; i < Count; i++)
                instanceValues[i] = FSharpCollection.FromEditable(this[i], _collectionType);
        }

        /// <inheritdoc />
        public override void RefreshReferenceValue(object instanceValue)
        {
            _referenceValue = FSharpCollection.ToEditable(instanceValue, _collectionType);
            _hasReferenceValue = true;
        }
    }
}
