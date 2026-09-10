// Copyright (c) Wojciech Figat. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FlaxEditor.Content
{
    /// <summary>
    /// Keeps the Compile list of an F# project (.fsproj) in step with files created, renamed and
    /// deleted from the editor.
    /// </summary>
    /// <remarks>
    /// Unlike C#, F# only compiles files listed in the project, in the listed order, and a file
    /// can only use what is declared in files above it. So a new file is appended at the end (it
    /// may depend on everything that exists) and a renamed file keeps its position.
    /// The project is edited as text rather than through an XML DOM so that everything the user
    /// wrote - formatting, comments, line endings, encoding - stays byte-for-byte as it was.
    /// </remarks>
    public static class FSharpProjectFile
    {
        private static readonly Regex CompileItem = new Regex(
            "<Compile\\s+Include\\s*=\\s*\"(?<path>[^\"]*)\"[^>]*?(?:/>|>.*?</Compile\\s*>)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        /// <summary>
        /// Finds the F# project that owns the given source file: the nearest folder upwards that
        /// contains exactly one .fsproj, searching no higher than the project's Source folder.
        /// </summary>
        /// <param name="sourceFile">The source file path (it does not have to exist yet).</param>
        /// <returns>The project file path, or null if there is none or it is ambiguous.</returns>
        public static string FindProject(string sourceFile)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(sourceFile));
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(dir))
                {
                    var projects = Directory.GetFiles(dir, "*.fsproj", SearchOption.TopDirectoryOnly);
                    if (projects.Length == 1)
                        return projects[0];
                    if (projects.Length > 1)
                        return null;
                }
                if (string.Equals(Path.GetFileName(dir), "Source", StringComparison.OrdinalIgnoreCase))
                    return null;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        /// <summary>
        /// Gets the Include value for a source file, relative to the project folder.
        /// </summary>
        public static string GetInclude(string projectFile, string sourceFile)
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFile));
            return Path.GetRelativePath(projectDir, Path.GetFullPath(sourceFile)).Replace('\\', '/');
        }

        /// <summary>
        /// Lists the Compile items of the project text, in compilation order.
        /// </summary>
        public static List<string> GetCompileItems(string projectXml)
        {
            var result = new List<string>();
            foreach (Match m in CompileItem.Matches(projectXml))
                result.Add(m.Groups["path"].Value);
            return result;
        }

        /// <summary>
        /// Appends a Compile item after the last existing one. Returns the text unchanged if the
        /// file is already listed.
        /// </summary>
        public static string AddCompileItem(string projectXml, string include)
        {
            var matches = CompileItem.Matches(projectXml);
            foreach (Match m in matches)
            {
                if (SamePath(m.Groups["path"].Value, include))
                    return projectXml;
            }

            var newLine = DetectNewLine(projectXml);
            var element = $"<Compile Include=\"{include}\" />";
            if (matches.Count != 0)
            {
                var last = matches[matches.Count - 1];
                var indent = GetLineIndent(projectXml, last.Index);
                return projectXml.Insert(last.Index + last.Length, newLine + indent + element);
            }

            var close = projectXml.LastIndexOf("</Project>", StringComparison.Ordinal);
            if (close < 0)
                throw new FormatException("Not an MSBuild project: missing </Project>.");
            var lineStart = projectXml.LastIndexOf('\n', Math.Max(close - 1, 0)) + 1;
            if (!string.IsNullOrWhiteSpace(projectXml.Substring(lineStart, close - lineStart)))
                lineStart = close;
            var block = "  <ItemGroup>" + newLine + "    " + element + newLine + "  </ItemGroup>" + newLine;
            return projectXml.Insert(lineStart, block);
        }

        /// <summary>
        /// Removes a Compile item (and its line, if it was alone on it). Returns the text
        /// unchanged if the file is not listed.
        /// </summary>
        public static string RemoveCompileItem(string projectXml, string include)
        {
            foreach (Match m in CompileItem.Matches(projectXml))
            {
                if (!SamePath(m.Groups["path"].Value, include))
                    continue;

                int start = m.Index, end = m.Index + m.Length;
                var lineStart = start == 0 ? 0 : projectXml.LastIndexOf('\n', start - 1) + 1;
                var lineEnd = projectXml.IndexOf('\n', end);
                if (lineEnd < 0)
                    lineEnd = projectXml.Length - 1;
                if (string.IsNullOrWhiteSpace(projectXml.Substring(lineStart, start - lineStart)) &&
                    string.IsNullOrWhiteSpace(projectXml.Substring(end, lineEnd + 1 - end)))
                {
                    start = lineStart;
                    end = lineEnd + 1;
                }
                return projectXml.Remove(start, end - start);
            }
            return projectXml;
        }

        /// <summary>
        /// Renames a Compile item in place, keeping its position in the compilation order.
        /// Returns the text unchanged if the old file is not listed.
        /// </summary>
        public static string RenameCompileItem(string projectXml, string oldInclude, string newInclude)
        {
            foreach (Match m in CompileItem.Matches(projectXml))
            {
                var group = m.Groups["path"];
                if (SamePath(group.Value, oldInclude))
                    return projectXml.Remove(group.Index, group.Length).Insert(group.Index, newInclude);
            }
            return projectXml;
        }

        /// <summary>
        /// Adds a source file to its owning F# project.
        /// </summary>
        /// <returns>True if the file is now listed in a project, false if no project owns it.</returns>
        public static bool AddToProject(string sourceFile)
        {
            var project = FindProject(sourceFile);
            if (project == null)
                return false;
            Update(project, xml => AddCompileItem(xml, GetInclude(project, sourceFile)));
            return true;
        }

        /// <summary>
        /// Removes a source file from its owning F# project, if any.
        /// </summary>
        public static void RemoveFromProject(string sourceFile)
        {
            var project = FindProject(sourceFile);
            if (project != null)
                Update(project, xml => RemoveCompileItem(xml, GetInclude(project, sourceFile)));
        }

        /// <summary>
        /// Follows a source file rename or move. Within one project the item keeps its position;
        /// across projects it is removed from the old one and appended to the new one.
        /// </summary>
        public static void RenameInProject(string oldFile, string newFile)
        {
            var oldProject = FindProject(oldFile);
            var newProject = FindProject(newFile);
            if (oldProject != null && newProject != null &&
                string.Equals(Path.GetFullPath(oldProject), Path.GetFullPath(newProject), StringComparison.OrdinalIgnoreCase))
            {
                Update(oldProject, xml => RenameCompileItem(xml, GetInclude(oldProject, oldFile), GetInclude(oldProject, newFile)));
                return;
            }
            if (oldProject != null)
                Update(oldProject, xml => RemoveCompileItem(xml, GetInclude(oldProject, oldFile)));
            if (newProject != null)
                Update(newProject, xml => AddCompileItem(xml, GetInclude(newProject, newFile)));
        }

        private static void Update(string projectFile, Func<string, string> edit)
        {
            var bytes = File.ReadAllBytes(projectFile);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = Encoding.UTF8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
            var updated = edit(text);
            if (string.Equals(updated, text, StringComparison.Ordinal))
                return;
            // Encoding.UTF8 writes a BOM; the encoding-less overload writes UTF-8 without one.
            if (hasBom)
                File.WriteAllText(projectFile, updated, Encoding.UTF8);
            else
                File.WriteAllText(projectFile, updated);
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(NormalizeInclude(a), NormalizeInclude(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeInclude(string value)
        {
            value = value.Trim().Replace('\\', '/');
            while (value.StartsWith("./", StringComparison.Ordinal))
                value = value.Substring(2);
            return value;
        }

        private static string DetectNewLine(string text)
        {
            return text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        }

        private static string GetLineIndent(string text, int index)
        {
            var lineStart = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
            var sb = new StringBuilder();
            for (int i = lineStart; i < index && (text[i] == ' ' || text[i] == '\t'); i++)
                sb.Append(text[i]);
            return sb.ToString();
        }
    }
}
