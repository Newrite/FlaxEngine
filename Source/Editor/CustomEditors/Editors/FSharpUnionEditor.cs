// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Linq;
using FlaxEditor.GUI;
using FlaxEditor.Scripting;
using Microsoft.FSharp.Reflection;

namespace FlaxEditor.CustomEditors.Editors
{
    /// <summary>
    /// Default implementation of the inspector used to edit F# discriminated unions (option and Result included): a case picker and the fields of the chosen case.
    /// </summary>
    /// <seealso cref="FlaxEditor.CustomEditors.CustomEditor" />
    public sealed class FSharpUnionEditor : CustomEditor
    {
        private Type _type;
        private UnionCaseInfo[] _cases;
        private string _case;

        /// <inheritdoc />
        public override bool RevertValueWithChildren => false; // Always revert a whole union value

        /// <inheritdoc />
        public override void Initialize(LayoutElementsContainer layout)
        {
            _type = Values.Type.Type;
            _cases = FSharpUnion.GetCases(_type);
            _case = GetCurrentCase(out var hasDifferentCases);

            // Case
            var caseItem = layout.AddPropertyItem("Case", "The case of the union value. Use it to change the value to a different case.");
            var caseEditor = caseItem.ComboBox();
            foreach (var unionCase in _cases)
                caseEditor.ComboBox.AddItem(unionCase.Name);
            caseEditor.ComboBox.SupportMultiSelect = false;
            caseEditor.ComboBox.SelectedIndex = Array.FindIndex(_cases, x => x.Name == _case);
            caseEditor.ComboBox.SelectedIndexChanged += OnSelectedIndexChanged;

            if (hasDifferentCases)
            {
                layout.AddPropertyItem("Value").Label("Different Values");
                return;
            }
            if (_case == null)
                return;

            // Fields of the current case
            var caseInfo = _cases.First(x => x.Name == _case);
            foreach (var field in caseInfo.GetFields())
            {
                var values = new FSharpUnionFieldValueContainer(new ScriptMemberInfo(field), Values, _type);
                layout.Property(Utilities.Utils.GetPropertyNameUI(field.Name), values);
            }
        }

        private void OnSelectedIndexChanged(ComboBox comboBox)
        {
            if (comboBox.SelectedIndex != -1)
                SelectCase(_cases[comboBox.SelectedIndex].Name);
        }

        /// <summary>
        /// Changes the value to the given case with default field values.
        /// </summary>
        /// <param name="caseName">The case name.</param>
        internal void SelectCase(string caseName)
        {
            if (caseName == _case)
                return;
            SetValue(FSharpUnion.CreateCase(_type, caseName));
        }

        private string GetCurrentCase(out bool hasDifferentCases)
        {
            hasDifferentCases = false;
            string result = null;
            for (int i = 0; i < Values.Count; i++)
            {
                var name = FSharpUnion.GetCaseName(Values[i], _type);
                if (i == 0)
                    result = name;
                else if (name != result)
                {
                    hasDifferentCases = true;
                    return null;
                }
            }
            return result;
        }

        /// <inheritdoc />
        public override void Refresh()
        {
            base.Refresh();

            // The case changed (picked here, undone, or set from code): show the fields of the new case
            if (GetCurrentCase(out _) != _case)
            {
                if (ParentEditor != null)
                    ParentEditor.RebuildLayout();
                else
                    RebuildLayout();
            }
        }
    }
}
