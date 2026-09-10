// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.IO;
using System.Text;
using FlaxEditor.Content.Settings;
using FlaxEngine;

namespace FlaxEditor.Content
{
    /// <summary>
    /// Proxy object for F# files.
    /// </summary>
    /// <seealso cref="FlaxEditor.Content.ScriptProxy" />
    public abstract class FSharpProxy : ScriptProxy
    {
        /// <inheritdoc />
        public override bool IsProxyFor(ContentItem item)
        {
            return item is FSharpScriptItem;
        }

        /// <summary>
        /// Gets the path for the F# template.
        /// </summary>
        /// <param name="path">The path to the template</param>
        protected abstract void GetTemplatePath(out string path);

        /// <inheritdoc />
        public override ContentItem ConstructItem(string path)
        {
            return new FSharpScriptItem(path);
        }

        /// <inheritdoc />
        public override bool CanCreate(ContentFolder targetLocation)
        {
            // Only inside an F# project: a lone .fs file anywhere else is compiled by nothing.
            return base.CanCreate(targetLocation) &&
                   FSharpProjectFile.FindProject(Path.Combine(targetLocation.Path, "New.fs")) != null;
        }

        /// <inheritdoc />
        public override void Create(string outputPath, object arg)
        {
            // Load template
            GetTemplatePath(out var templatePath);
            var scriptTemplate = File.ReadAllText(templatePath);

            // Find the module that this script is being added (based on the path)
            var scriptNamespace = Editor.Instance.GameProject.Name;
            var project = TryGetProjectAtFolder(outputPath, out var moduleName);
            if (project != null)
            {
                scriptNamespace = moduleName.Length != 0 ? moduleName : project.Name;
            }
            scriptNamespace = scriptNamespace.Replace(" ", "");

            // Format
            var gameSettings = GameSettings.Load();
            var scriptName = ScriptItem.CreateScriptName(outputPath);
            var copyrightComment = string.IsNullOrEmpty(gameSettings.CopyrightNotice) ? string.Empty : string.Format("// {0}{1}{1}", gameSettings.CopyrightNotice, Environment.NewLine);
            scriptTemplate = scriptTemplate.Replace("%copyright%", copyrightComment);
            scriptTemplate = scriptTemplate.Replace("%class%", scriptName);
            scriptTemplate = scriptTemplate.Replace("%namespace%", scriptNamespace);

            // Save
            File.WriteAllText(outputPath, scriptTemplate);

            // F# compiles only what the project lists, so a file that is not added is invisible
            if (!FSharpProjectFile.AddToProject(outputPath))
                Editor.LogWarning($"No F# project (.fsproj) found for '{outputPath}'. Add it to the project's Compile list manually.");
        }

        /// <inheritdoc />
        public override string FileExtension => "fs";

        /// <inheritdoc />
        public override Color AccentColor => Color.FromRGB(0x378bba);
    }

    /// <summary>
    /// Context proxy object for F# Script files.
    /// </summary>
    /// <seealso cref="FlaxEditor.Content.FSharpProxy" />
    [ContentContextMenu("New/F#/F# Script")]
    public class FSharpScriptProxy : FSharpProxy
    {
        /// <inheritdoc />
        public override string Name => "F# Script";

        /// <inheritdoc />
        protected override void GetTemplatePath(out string path)
        {
            path = StringUtils.CombinePaths(Globals.EngineContentFolder, "Editor/Scripting/FSharpScriptTemplate.fs");
        }
    }

    /// <summary>
    /// Context proxy object for F# module files.
    /// </summary>
    /// <seealso cref="FlaxEditor.Content.FSharpProxy" />
    [ContentContextMenu("New/F#/F# Module")]
    public class FSharpModuleProxy : FSharpProxy
    {
        /// <inheritdoc />
        public override string Name => "F# Module";

        /// <inheritdoc />
        public override string NewItemName => "MyModule";

        /// <inheritdoc />
        protected override void GetTemplatePath(out string path)
        {
            path = StringUtils.CombinePaths(Globals.EngineContentFolder, "Editor/Scripting/FSharpModuleTemplate.fs");
        }
    }

    /// <summary>
    /// Context proxy object for F# Actor files.
    /// </summary>
    /// <seealso cref="FlaxEditor.Content.FSharpProxy" />
    [ContentContextMenu("New/F#/F# Actor")]
    public class FSharpActorProxy : FSharpProxy
    {
        /// <inheritdoc />
        public override string Name => "F# Actor";

        /// <inheritdoc />
        protected override void GetTemplatePath(out string path)
        {
            path = StringUtils.CombinePaths(Globals.EngineContentFolder, "Editor/Scripting/FSharpActorTemplate.fs");
        }
    }

    /// <summary>
    /// Context proxy object for F# GamePlugin files.
    /// </summary>
    /// <seealso cref="FlaxEditor.Content.FSharpProxy" />
    [ContentContextMenu("New/F#/F# GamePlugin")]
    public class FSharpGamePluginProxy : FSharpProxy
    {
        /// <inheritdoc />
        public override string Name => "F# GamePlugin";

        /// <inheritdoc />
        protected override void GetTemplatePath(out string path)
        {
            path = StringUtils.CombinePaths(Globals.EngineContentFolder, "Editor/Scripting/FSharpGamePluginTemplate.fs");
        }
    }
}
