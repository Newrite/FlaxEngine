// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using FlaxEngine;

namespace FlaxEditor.Content
{
    /// <summary>
    /// Content item that contains F# file with source code.
    /// </summary>
    /// <seealso cref="FlaxEditor.Content.ScriptItem" />
    public class FSharpScriptItem : ScriptItem
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FSharpScriptItem"/> class.
        /// </summary>
        /// <param name="path">The path to the item.</param>
        public FSharpScriptItem(string path)
        : base(path)
        {
        }

        /// <inheritdoc />
        public override string TypeDescription => "F# Source Code";

        private static SpriteHandle _thumbnail;
        private static bool _thumbnailLoaded;

        /// <inheritdoc />
        public override SpriteHandle DefaultThumbnail
        {
            get
            {
                // The F# script icon, in the style of the C# and C++ ones of the editor icons atlas, comes from its own small atlas next to the F# templates
                if (!_thumbnailLoaded)
                {
                    _thumbnailLoaded = true;
                    var atlas = FlaxEngine.Content.LoadAsync<SpriteAtlas>(StringUtils.CombinePaths(Globals.EngineContentFolder, "Editor/Scripting/FSharpIcons.flax"));
                    if (atlas != null && !atlas.WaitForLoaded())
                        _thumbnail = atlas.FindSprite("FSharpScript128");
                }
                return _thumbnail.IsValid ? _thumbnail : Editor.Instance.Icons.Document128;
            }
        }

        /// <inheritdoc />
        internal override void UpdatePath(string value)
        {
            // An F# file that is renamed or moved without its project entry following it breaks
            // the build: the project would list a file that no longer exists.
            var oldPath = Path;
            base.UpdatePath(value);
            if (oldPath != null && !string.Equals(oldPath, Path, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    FSharpProjectFile.RenameInProject(oldPath, Path);
                }
                catch (Exception ex)
                {
                    Editor.LogWarning($"Failed to update the F# project after renaming '{oldPath}' to '{Path}'.");
                    Editor.LogWarning(ex);
                }
            }
        }
    }
}
