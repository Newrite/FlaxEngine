// Copyright (c) Wojciech Figat. All rights reserved.

#if FLAX_TESTS
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using FlaxEditor.Content;
using NUnit.Framework;

namespace FlaxEngine.Tests
{
    /// <summary>
    /// Tests for <see cref="FSharpProjectFile"/>.
    /// </summary>
    /// <remarks>
    /// F# compiles only the files listed in the project, in the listed order, so a lost or reordered entry breaks the build.
    /// The project file belongs to the user, so nothing else in it may change.
    /// </remarks>
    [TestFixture]
    public class TestFSharpProjectFile
    {
        private const string Project =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
            "  <!-- keep me -->\r\n" +
            "  <ItemGroup>\r\n" +
            "    <Compile Include=\"Utils.fs\" />\r\n" +
            "    <Compile Include=\"MyUtils.fs\" />\r\n" +
            "    <Compile Include=\"Sub\\Player.fs\" />\r\n" +
            "  </ItemGroup>\r\n" +
            "  <ItemGroup>\r\n" +
            "    <Reference Include=\"FlaxEngine.CSharp\" />\r\n" +
            "  </ItemGroup>\r\n" +
            "</Project>\r\n";

        private string _root;

        private string ProjectPath => Path.Combine(_root, "Source", "GameFSharp", "GameFSharp.fsproj");

        private static string[] Items(string xml)
        {
            return FSharpProjectFile.GetCompileItems(xml).ToArray();
        }

        private static void AssertWellFormed(string xml)
        {
            var doc = new XmlDocument();
            Assert.DoesNotThrow(() => doc.LoadXml(xml));
        }

        [SetUp]
        public void CreateTree()
        {
            _root = Path.Combine(Path.GetTempPath(), "FlaxTestFSharpProjectFile_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "Source", "GameFSharp", "Enemies"));
            Directory.CreateDirectory(Path.Combine(_root, "Source", "Game"));
        }

        [TearDown]
        public void DeleteTree()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Test that a new file is appended after the last Compile item, keeping indentation and line endings.
        /// </summary>
        [Test]
        public void TestAdd()
        {
            var result = FSharpProjectFile.AddCompileItem(Project, "Enemy.fs");

            CollectionAssert.AreEqual(new[] { "Utils.fs", "MyUtils.fs", "Sub\\Player.fs", "Enemy.fs" }, Items(result));
            Assert.AreEqual(Project, result.Replace("    <Compile Include=\"Enemy.fs\" />\r\n", ""));
            AssertWellFormed(result);
        }

        /// <summary>
        /// Test that adding an already listed file (with other slashes and case) does nothing.
        /// </summary>
        [Test]
        public void TestAddExisting()
        {
            Assert.AreEqual(Project, FSharpProjectFile.AddCompileItem(Project, "sub/player.fs"));
        }

        /// <summary>
        /// Test adding to a project without any Compile items.
        /// </summary>
        [Test]
        public void TestAddToEmptyProject()
        {
            const string empty = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup />\n</Project>\n";

            var result = FSharpProjectFile.AddCompileItem(empty, "First.fs");

            CollectionAssert.AreEqual(new[] { "First.fs" }, Items(result));
            Assert.IsFalse(result.Contains("\r\n"));
            AssertWellFormed(result);
        }

        /// <summary>
        /// Test that removing deletes only that line, matching the whole path rather than a suffix.
        /// </summary>
        [Test]
        public void TestRemove()
        {
            Assert.AreEqual(Project.Replace("    <Compile Include=\"MyUtils.fs\" />\r\n", ""), FSharpProjectFile.RemoveCompileItem(Project, "MyUtils.fs"));
            CollectionAssert.AreEqual(new[] { "MyUtils.fs", "Sub\\Player.fs" }, Items(FSharpProjectFile.RemoveCompileItem(Project, "Utils.fs")));
            Assert.AreEqual(Project, FSharpProjectFile.RemoveCompileItem(Project, "Ghost.fs"));
        }

        /// <summary>
        /// Test removing a Compile item written as a multi-line element.
        /// </summary>
        [Test]
        public void TestRemoveMultiLine()
        {
            var xml = Project.Replace("<Compile Include=\"MyUtils.fs\" />", "<Compile Include=\"MyUtils.fs\">\r\n      <Link>Shared\\MyUtils.fs</Link>\r\n    </Compile>");

            Assert.AreEqual(Project.Replace("    <Compile Include=\"MyUtils.fs\" />\r\n", ""), FSharpProjectFile.RemoveCompileItem(xml, "MyUtils.fs"));
        }

        /// <summary>
        /// Test that renaming keeps the file position in the compilation order.
        /// </summary>
        [Test]
        public void TestRename()
        {
            var result = FSharpProjectFile.RenameCompileItem(Project, "Utils.fs", "Core.fs");

            CollectionAssert.AreEqual(new[] { "Core.fs", "MyUtils.fs", "Sub\\Player.fs" }, Items(result));
        }

        /// <summary>
        /// Test finding the owning project: from a nested folder, never above the Source folder, and not when ambiguous.
        /// </summary>
        [Test]
        public void TestFindProject()
        {
            File.WriteAllText(ProjectPath, Project);
            Assert.AreEqual(Path.GetFullPath(ProjectPath), FSharpProjectFile.FindProject(Path.Combine(_root, "Source", "GameFSharp", "Enemies", "Boss.fs")));

            File.WriteAllText(Path.Combine(_root, "Stray.fsproj"), Project);
            Assert.IsNull(FSharpProjectFile.FindProject(Path.Combine(_root, "Source", "Game", "Thing.fs")));

            File.WriteAllText(Path.Combine(_root, "Source", "GameFSharp", "Other.fsproj"), Project);
            Assert.IsNull(FSharpProjectFile.FindProject(Path.Combine(_root, "Source", "GameFSharp", "A.fs")));
        }

        /// <summary>
        /// Test adding a nested file to the project on disk: relative include and preserved UTF-8 BOM.
        /// </summary>
        [Test]
        public void TestAddToProjectFile()
        {
            // Encoding.UTF8 writes a BOM
            File.WriteAllText(ProjectPath, Project, Encoding.UTF8);

            Assert.IsTrue(FSharpProjectFile.AddToProject(Path.Combine(_root, "Source", "GameFSharp", "Enemies", "Boss.fs")));

            var bytes = File.ReadAllBytes(ProjectPath);
            Assert.IsTrue(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.AreEqual("Enemies/Boss.fs", Items(File.ReadAllText(ProjectPath)).Last());
            Assert.IsFalse(FSharpProjectFile.AddToProject(Path.Combine(_root, "Source", "Game", "Thing.fs")));
        }

        /// <summary>
        /// Test following a file move: within the project it keeps its position, out of the project it is removed.
        /// </summary>
        [Test]
        public void TestRenameInProjectFile()
        {
            File.WriteAllText(ProjectPath, Project);
            var dir = Path.Combine(_root, "Source", "GameFSharp");

            FSharpProjectFile.RenameInProject(Path.Combine(dir, "Utils.fs"), Path.Combine(dir, "Enemies", "Utils.fs"));
            CollectionAssert.AreEqual(new[] { "Enemies/Utils.fs", "MyUtils.fs", "Sub\\Player.fs" }, Items(File.ReadAllText(ProjectPath)));

            FSharpProjectFile.RenameInProject(Path.Combine(dir, "MyUtils.fs"), Path.Combine(_root, "Source", "Game", "MyUtils.fs"));
            CollectionAssert.AreEqual(new[] { "Enemies/Utils.fs", "Sub\\Player.fs" }, Items(File.ReadAllText(ProjectPath)));
        }
    }
}
#endif
