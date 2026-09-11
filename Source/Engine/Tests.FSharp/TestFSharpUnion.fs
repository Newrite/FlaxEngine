// Copyright (c) Wojciech Figat. All rights reserved.

namespace FlaxEngine.Tests.FSharp

open FlaxEditor.Scripting
open FlaxEngine.Tests.FSharp.Fixtures
open NUnit.Framework

/// Tests for FSharpUnion, which lets the inspector pick the case of an F# discriminated union and edit its fields.
[<TestFixture>]
type TestFSharpUnion() =

    /// Test which types the union editor takes: unions (option and Result included), but not F# list, which is a union
    /// too but must be edited as a collection.
    [<Test>]
    member _.TestIsEditableUnion() =
        Assert.IsTrue(FSharpUnion.IsEditableUnion(typeof<Shape>))
        Assert.IsTrue(FSharpUnion.IsEditableUnion(typeof<string option>))
        Assert.IsTrue(FSharpUnion.IsEditableUnion(typeof<Result<int, string>>))
        Assert.IsFalse(FSharpUnion.IsEditableUnion(typeof<int list>), "an F# list is a union but must be edited as a collection")
        Assert.IsFalse(FSharpUnion.IsEditableUnion(typeof<PlainRecord>))
        Assert.IsFalse(FSharpUnion.IsEditableUnion(null))

    /// Test reading the case of a value, including None stored as null.
    [<Test>]
    member _.TestGetCaseName() =
        Assert.AreEqual("Rect", FSharpUnion.GetCaseName(Rect(1.0f, 2.0f), typeof<Shape>))
        Assert.AreEqual("Some", FSharpUnion.GetCaseName(Some "x", typeof<string option>))
        Assert.AreEqual("None", FSharpUnion.GetCaseName(null, typeof<string option>), "None is stored as null")
        Assert.IsNull(FSharpUnion.GetCaseName(null, typeof<Shape>), "null is no case of a union that does not use null for one")

    /// Test creating a case with default field values.
    [<Test>]
    member _.TestCreateCase() =
        Assert.AreEqual(box (Rect(0.0f, 0.0f)), FSharpUnion.CreateCase(typeof<Shape>, "Rect"))
        Assert.AreEqual(box (Some ""), FSharpUnion.CreateCase(typeof<string option>, "Some"))
        Assert.IsNull(FSharpUnion.CreateCase(typeof<string option>, "None"))

    /// Test building a copy of a union value with one field changed.
    [<Test>]
    member _.TestWithField() =
        Assert.AreEqual(box (Rect(5.0f, 2.0f)), FSharpUnion.WithField(Rect(1.0f, 2.0f), typeof<Shape>, "width", box 5.0f))
        Assert.AreEqual(box (Some "hi"), FSharpUnion.WithField(Some "x", typeof<string option>, "Value", box "hi"))

    /// Test that a record's union field defaults to the first case - a usable value, not null.
    [<Test>]
    member _.TestRecordDefaultWithUnionField() =
        let created = FSharpRecord.CreateDefault(typeof<Tagged>) :?> Tagged

        Assert.AreEqual(box (Circle 0.0f), box created.Kind, "a union field defaults to its first case")
        Assert.AreEqual(box (None: string option), box created.Label)
