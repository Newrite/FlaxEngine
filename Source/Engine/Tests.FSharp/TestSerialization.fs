// Copyright (c) Wojciech Figat. All rights reserved.

namespace FlaxEngine.Tests.FSharp

open System.Reflection
open FlaxEngine.Json
open FlaxEngine.Tests.FSharp.Fixtures
open NUnit.Framework

/// Tests for JsonSerializer with F# types.
[<TestFixture>]
type TestSerialization() =

    static let roundTrip (value: 'T when 'T: (new: unit -> 'T)) : 'T =
        let json = JsonSerializer.Serialize(box value)
        let clone = new 'T()
        JsonSerializer.Deserialize(box clone, json)
        clone

    static let areEqual (expected: 'T) (actual: 'T) = Assert.AreEqual(box expected, box actual)

    /// Test `[<DefaultValue>] val mutable` fields round trip and are written to the document.
    [<Test>]
    member _.TestMutableValField() =
        let source = WithMutableField(Speed = 42.5f, Label = "hello")

        let json = JsonSerializer.Serialize(source)
        StringAssert.Contains("\"Speed\"", json)

        let clone = roundTrip source
        areEqual 42.5f clone.Speed
        areEqual "hello" clone.Label

    /// Test `member val` auto-properties round trip.
    [<Test>]
    member _.TestMemberVal() =
        let clone = roundTrip (WithMemberVal(Speed = 3.25f, Label = "auto"))

        areEqual 3.25f clone.Speed
        areEqual "auto" clone.Label

    /// Test that a bare `let mutable` (a private field) is not serialized.
    [<Test>]
    member _.TestLetMutableIsNotSerialized() =
        let source = WithLetMutable()
        source.Poke 99.0f

        StringAssert.DoesNotContain("99", JsonSerializer.Serialize(source))

    /// Test option round trip.
    [<Test>]
    member _.TestOption() =
        let some = roundTrip (WithOption(MaybeName = Some "present"))
        areEqual (Some "present") some.MaybeName

        let none = roundTrip (WithOption(MaybeName = None))
        areEqual None none.MaybeName

    /// Test discriminated union round trip.
    [<Test>]
    member _.TestUnion() =
        let source = WithUnion(Shape = Rect(3.0f, 4.0f))

        StringAssert.Contains("Rect", JsonSerializer.Serialize(source))
        areEqual (Rect(3.0f, 4.0f)) (roundTrip source).Shape

    /// Test list round trip.
    [<Test>]
    member _.TestList() =
        areEqual [ 1; 2; 3 ] (roundTrip (WithList(Numbers = [ 1; 2; 3 ]))).Numbers

    /// Test [<CLIMutable>] record round trip.
    [<Test>]
    member _.TestCLIMutableRecord() =
        let json = JsonSerializer.Serialize({ MutableRecord.Name = "widget"; Amount = 5 })

        areEqual { MutableRecord.Name = "widget"; Amount = 5 } (JsonSerializer.Deserialize<MutableRecord>(json))

    /// Test plain record round trip (it used to serialize as an empty object).
    [<Test>]
    member _.TestRecord() =
        let json = JsonSerializer.Serialize({ PlainRecord.Name = "widget"; Amount = 5 })

        areEqual { PlainRecord.Name = "widget"; Amount = 5 } (JsonSerializer.Deserialize<PlainRecord>(json))

    /// Test plain record member round trip when populating an existing object (the way scenes and scripts are loaded).
    [<Test>]
    member _.TestRecordMember() =
        let clone = roundTrip (WithRecord(Item = { Name = "widget"; Amount = 5 }, Trailing = 9))

        areEqual { PlainRecord.Name = "widget"; Amount = 5 } clone.Item
        areEqual 9 clone.Trailing

    /// Test Map round trip (it used to throw in the middle of deserialization and lose the members after it).
    [<Test>]
    member _.TestMap() =
        let source = WithMap(Table = Map [ "a", 1; "b", 2 ], Trailing = 1234)
        let json = JsonSerializer.Serialize(source)
        Assert.Less(json.IndexOf("\"Table\""), json.IndexOf("\"Trailing\""), "precondition: the member under test follows the Map")

        let clone = roundTrip source

        areEqual (Map [ "a", 1; "b", 2 ]) clone.Table
        areEqual 1234 clone.Trailing

    /// Test Map with non-string keys round trip.
    [<Test>]
    member _.TestMapIntKeys() =
        areEqual (Map [ 3, "three"; 1, "one" ]) (roundTrip (WithIntKeyMap(Table = Map [ 3, "three"; 1, "one" ]))).Table

    /// Test empty Map round trip.
    [<Test>]
    member _.TestMapEmpty() =
        areEqual Map.empty<string, int> (roundTrip (WithMap(Table = Map.empty))).Table

    /// Test Set round trip (it used to be dropped silently).
    [<Test>]
    member _.TestSet() =
        let clone = roundTrip (WithSet(Tags = set [ "x"; "y" ], Trailing = 7))

        areEqual (set [ "x"; "y" ]) clone.Tags
        areEqual 7 clone.Trailing

    /// Test that the F# Map/Set converters do not claim .NET dictionaries and sets.
    [<Test>]
    member _.TestDotNetCollectionsUnaffected() =
        let dictionary = WithDictionary(Trailing = 99)
        dictionary.Table.["a"] <- 1
        dictionary.Table.["b"] <- 2
        let dictionaryClone = roundTrip dictionary
        areEqual 2 dictionaryClone.Table.Count
        areEqual 1 dictionaryClone.Table.["a"]
        areEqual 99 dictionaryClone.Trailing

        let hashSet = WithHashSet(Trailing = 42)
        hashSet.Tags.Add "x" |> ignore
        hashSet.Tags.Add "y" |> ignore
        let hashSetClone = roundTrip hashSet
        areEqual 2 hashSetClone.Tags.Count
        areEqual 42 hashSetClone.Trailing

    /// Test that an immutable class which is not a record keeps its get-only properties out of the document.
    [<Test>]
    member _.TestImmutableClassIsNotRecord() =
        let json = JsonSerializer.Serialize(ImmutableClass("widget", 5))

        StringAssert.DoesNotContain("\"Name\"", json)
        StringAssert.DoesNotContain("\"Amount\"", json)

    /// Test that resetting the serializer cache drops Newtonsoft's cached FSharp.Core bindings, which otherwise
    /// keep the first loaded FSharp.Core (and its scripting load context) across scripts reloads.
    [<Test>]
    member _.TestResetCacheReleasesFSharpUtils() =
        // Serializing a union makes Newtonsoft create and cache the bindings
        JsonSerializer.Serialize(WithUnion()) |> ignore

        let instance =
            typeof<Newtonsoft.Json.JsonConvert>.Assembly
                .GetType("Newtonsoft.Json.Utilities.FSharpUtils")
                .GetField("_instance", BindingFlags.Static ||| BindingFlags.NonPublic)

        Assert.IsNotNull(instance.GetValue(null), "precondition: serializing a union caches the bindings")

        let resetCache = typeof<JsonSerializer>.GetMethod("ResetCache", BindingFlags.Static ||| BindingFlags.NonPublic)
        resetCache.Invoke(null, [||]) |> ignore

        Assert.IsNull(instance.GetValue(null))
