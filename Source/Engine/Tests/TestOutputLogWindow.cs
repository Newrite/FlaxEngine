// Copyright (c) Wojciech Figat. All rights reserved.

#if FLAX_TESTS
using System.Text.RegularExpressions;
using FlaxEditor.Windows;
using NUnit.Framework;

namespace FlaxEngine.Tests
{
    /// <summary>
    /// Tests for <see cref="OutputLogWindow"/> compiler diagnostics parsing.
    /// </summary>
    [TestFixture]
    public class TestOutputLogWindow
    {
        private static Match MatchDiagnostic(string line)
        {
            return new Regex(OutputLogWindow.CompileRegexPattern).Match(line);
        }

        /// <summary>
        /// Test matching csc diagnostics.
        /// </summary>
        [Test]
        public void TestCSharpError()
        {
            var match = MatchDiagnostic(@"F:\Game\Source\Game\Thing.cs(12,17,12,24): error CS0103: The name 'foo' does not exist in the current context");

            Assert.IsTrue(match.Success);
            Assert.AreEqual(@"F:\Game\Source\Game\Thing.cs", match.Groups["path"].Value);
            Assert.AreEqual("12", match.Groups["line"].Value);
            Assert.AreEqual("error", match.Groups["level"].Value);
        }

        /// <summary>
        /// Test matching fsc diagnostics, which put a subcategory word before the severity.
        /// </summary>
        [Test]
        public void TestFSharpDiagnostics()
        {
            var error = MatchDiagnostic(@"F:\Game\Source\GameFSharp\Bad.fs(2,15,2,27): typecheck error FS0001: This expression was expected to have type 'int'");
            Assert.IsTrue(error.Success);
            Assert.AreEqual(@"F:\Game\Source\GameFSharp\Bad.fs", error.Groups["path"].Value);
            Assert.AreEqual("2", error.Groups["line"].Value);
            Assert.AreEqual("error", error.Groups["level"].Value);

            var warning = MatchDiagnostic(@"F:\Game\Source\GameFSharp\Bad.fs(7,5,7,9): typecheck warning FS0064: This construct causes code to be less generic");
            Assert.IsTrue(warning.Success);
            Assert.AreEqual("warning", warning.Groups["level"].Value);
        }

        /// <summary>
        /// Test that lines which are not diagnostics are not matched.
        /// </summary>
        [Test]
        public void TestNotDiagnostic()
        {
            Assert.IsFalse(MatchDiagnostic(@"F:\Game\Source\GameFSharp\Bad.fs(2,15): error FS0001: two-number location").Success);
            Assert.IsFalse(MatchDiagnostic("Building target Game in Development for Windows x64").Success);
        }
    }
}
#endif
