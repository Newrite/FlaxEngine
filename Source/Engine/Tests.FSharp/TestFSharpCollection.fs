// Copyright (c) Wojciech Figat. All rights reserved.

namespace FlaxEngine.Tests.FSharp

open System.Collections.Generic
open FlaxEditor.Scripting
open FlaxEngine.Tests.FSharp.Fixtures
open FlaxEngine.Utilities
open NUnit.Framework

/// Tests for FSharpCollection, which lets the inspector edit immutable F# lists, maps and sets through a mutable
/// copy (an array or a Dictionary) that is built back into a new F# collection on every change.
[<TestFixture>]
type TestFSharpCollection() =

    /// Test recognizing the F# collections, and only them.
    [<Test>]
    member _.TestIsCollection() =
        Assert.IsTrue(FSharpCollection.IsList(typeof<int list>))
        Assert.IsTrue(FSharpCollection.IsMap(typeof<Map<string, int>>))
        Assert.IsTrue(FSharpCollection.IsSet(typeof<Set<string>>))
        Assert.IsTrue(FSharpCollection.IsCollection(typeof<PlainRecord list>))
        Assert.IsFalse(FSharpCollection.IsCollection(typeof<int[]>), "arrays already have an editor")
        Assert.IsFalse(FSharpCollection.IsCollection(typeof<ResizeArray<int>>), "List<T> already has an editor")
        Assert.IsFalse(FSharpCollection.IsCollection(typeof<Shape>))
        Assert.IsFalse(FSharpCollection.IsCollection(null))

    /// Test the mutable type each collection is edited as: the type the stock collection editors take.
    [<Test>]
    member _.TestGetEditableType() =
        Assert.AreEqual(typeof<int[]>, FSharpCollection.GetEditableType(typeof<int list>))
        Assert.AreEqual(typeof<string[]>, FSharpCollection.GetEditableType(typeof<Set<string>>))
        Assert.AreEqual(typeof<Dictionary<string, int>>, FSharpCollection.GetEditableType(typeof<Map<string, int>>))

    /// Test that a list is edited as an array in list order and built back from it.
    [<Test>]
    member _.TestList() =
        let editable = FSharpCollection.ToEditable([ 3; 1; 2 ], typeof<int list>) :?> int[]

        CollectionAssert.AreEqual([| 3; 1; 2 |], editable)
        Assert.AreEqual(box [ 5; 4 ], FSharpCollection.FromEditable([| 5; 4 |], typeof<int list>))

    /// Test that a set is edited as an array in set order, and that building it back applies set semantics.
    [<Test>]
    member _.TestSet() =
        let editable = FSharpCollection.ToEditable(set [ "b"; "a" ], typeof<Set<string>>) :?> string[]

        CollectionAssert.AreEqual([| "a"; "b" |], editable)
        Assert.AreEqual(box (set [ "a"; "b" ]), FSharpCollection.FromEditable([| "b"; "a"; "b" |], typeof<Set<string>>))

    /// Test that a map is edited as a Dictionary and built back from it.
    [<Test>]
    member _.TestMap() =
        let editable = FSharpCollection.ToEditable(Map [ "b", 2; "a", 1 ], typeof<Map<string, int>>) :?> Dictionary<string, int>

        CollectionAssert.AreEqual([| "a"; "b" |], editable.Keys)
        Assert.AreEqual(1, editable.["a"])
        editable.["c"] <- 3
        Assert.AreEqual(box (Map [ "a", 1; "b", 2; "c", 3 ]), FSharpCollection.FromEditable(editable, typeof<Map<string, int>>))

    /// Test that a collection member left null (eg. a [<DefaultValue>] field) is edited as empty and stored as a
    /// real empty collection: null is not a valid F# list, map or set.
    [<Test>]
    member _.TestNullIsEmpty() =
        CollectionAssert.IsEmpty(FSharpCollection.ToEditable(null, typeof<int list>) :?> int[])
        CollectionAssert.IsEmpty(FSharpCollection.ToEditable(null, typeof<Map<string, int>>) :?> Dictionary<string, int>)
        Assert.AreEqual(box ([]: int list), FSharpCollection.FromEditable(null, typeof<int list>))
        Assert.AreEqual(box (Set.empty: Set<string>), FSharpCollection.FromEditable(null, typeof<Set<string>>))

    /// Test the value a collection editor adds for a new element: F# records, unions and collections have no
    /// parameterless constructor, but must still get a usable value rather than null.
    [<Test>]
    member _.TestDefaultElementValue() =
        Assert.AreEqual(box { PlainRecord.Name = ""; Amount = 0 }, TypeUtils.GetDefaultValue(ScriptType(typeof<PlainRecord>)))
        Assert.AreEqual(box (Circle 0.0f), TypeUtils.GetDefaultValue(ScriptType(typeof<Shape>)))
        Assert.AreEqual(box ([]: int list), TypeUtils.GetDefaultValue(ScriptType(typeof<int list>)))
