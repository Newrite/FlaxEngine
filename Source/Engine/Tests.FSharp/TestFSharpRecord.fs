// Copyright (c) Wojciech Figat. All rights reserved.

namespace FlaxEngine.Tests.FSharp

open System
open FlaxEditor.Scripting
open FlaxEngine.Tests.FSharp.Fixtures
open NUnit.Framework

/// Tests for FSharpRecord, which lets the inspector edit immutable F# records by building updated copies.
[<TestFixture>]
type TestFSharpRecord() =

    /// Test recognizing F# records.
    [<Test>]
    member _.TestIsRecord() =
        Assert.IsTrue(FSharpRecord.IsRecord(typeof<PlainRecord>))
        Assert.IsTrue(FSharpRecord.IsRecord(typeof<MutableRecord>))
        Assert.IsFalse(FSharpRecord.IsRecord(typeof<Shape>), "a union is not a record")
        Assert.IsFalse(FSharpRecord.IsRecord(typeof<ImmutableClass>), "an immutable class is not a record")
        Assert.IsFalse(FSharpRecord.IsRecord(null))

    /// Test that record fields are listed in declaration order (the constructor parameter order).
    [<Test>]
    member _.TestGetFields() =
        let names = FSharpRecord.GetFields(typeof<Ordered>) |> Array.map (fun p -> p.Name)

        CollectionAssert.AreEqual([| "Zeta"; "Alpha" |], names)

    /// Test building a copy with one field changed.
    [<Test>]
    member _.TestWith() =
        let original = { PlainRecord.Name = "apple"; Amount = 1 }

        let updated = FSharpRecord.With(original, "Amount", box 5) :?> PlainRecord

        Assert.AreEqual(box { PlainRecord.Name = "apple"; Amount = 5 }, box updated)
        Assert.AreEqual(1, original.Amount, "the original record must not change")
        Assert.IsFalse(obj.ReferenceEquals(original, updated))

    /// Test building a copy of a record that holds another record.
    [<Test>]
    member _.TestWithNested() =
        let original = { Inner = { Name = "apple"; Amount = 1 }; Tag = "fruit" }

        let updated = FSharpRecord.With(original, "Inner", box { PlainRecord.Name = "pear"; Amount = 2 }) :?> Outer

        Assert.AreEqual(box { Inner = { Name = "pear"; Amount = 2 }; Tag = "fruit" }, box updated)

    /// Test creating a record with default values (what the inspector's "+" does for an unset record member):
    /// usable F# values, not nulls that break F# code - empty collections, None, zero, "", and nested records.
    [<Test>]
    member _.TestCreateDefault() =
        let created = FSharpRecord.CreateDefault(typeof<Defaults>) :?> Defaults

        Assert.AreEqual("", created.Text)
        Assert.AreEqual(0, created.Number)
        Assert.AreEqual(box ([]: int list), box created.Items)
        Assert.AreEqual(box (Map.empty: Map<string, int>), box created.Table)
        Assert.AreEqual(box (Set.empty: Set<string>), box created.Tags)
        Assert.AreEqual(box (None: string option), box created.Maybe)
        CollectionAssert.IsEmpty(created.Values)
        Assert.AreEqual(box { PlainRecord.Name = ""; Amount = 0 }, box created.Inner)

    /// Test that an unknown field name is an error rather than a silent no-op.
    [<Test>]
    member _.TestWithUnknownField() =
        Assert.Throws<ArgumentException>(fun () -> FSharpRecord.With({ PlainRecord.Name = "a"; Amount = 1 }, "Missing", box 1) |> ignore)
        |> ignore
