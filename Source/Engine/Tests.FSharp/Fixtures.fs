// Copyright (c) Wojciech Figat. All rights reserved.

/// F# types under test, one construct per type so a failing test names the construct that broke.
namespace FlaxEngine.Tests.FSharp.Fixtures

/// `[<DefaultValue>] val mutable public` fields.
type WithMutableField() =
    [<DefaultValue>]
    val mutable public Speed: float32

    [<DefaultValue>]
    val mutable public Label: string

/// `member val ... with get, set` auto-properties.
type WithMemberVal() =
    member val Speed = 0.0f with get, set
    member val Label: string = null with get, set

/// A bare `let mutable` is a private field, invisible to the serializer by design.
type WithLetMutable() =
    let mutable hidden = 0.0f
    member this.Poke value = hidden <- value
    member this.Peek() = hidden

/// A plain record: immutable, get-only properties, no parameterless constructor.
type PlainRecord = { Name: string; Amount: int }

/// A record whose field order is deliberately not alphabetical.
type Ordered = { Zeta: int; Alpha: string }

/// A record holding another record.
type Outer = { Inner: PlainRecord; Tag: string }

/// A record marked [<CLIMutable>]: gains a parameterless constructor and settable properties.
[<CLIMutable>]
type MutableRecord = { Name: string; Amount: int }

/// A discriminated union.
type Shape =
    | Circle of radius: float32
    | Rect of width: float32 * height: float32

type WithUnion() =
    member val Shape = Circle 1.0f with get, set

type WithOption() =
    member val MaybeName: string option = None with get, set

type WithList() =
    member val Numbers: int list = [] with get, set

type WithRecord() =
    [<DefaultValue>]
    val mutable public Item: PlainRecord

    [<DefaultValue>]
    val mutable public Trailing: int

// The collections below start out EMPTY rather than null, the way a script declares them. The serializer
// populates existing objects, so on load it meets that empty Map/Set and must replace it: populating an
// immutable F# collection in place is what threw (Map, losing every member after it) or silently dropped
// the data (Set). A null default would take Newtonsoft's create-new path and never hit the bug.

/// Map declared before another member, so the member order in the document is known.
type WithMap() =
    member val Table: Map<string, int> = Map.empty with get, set
    member val Trailing = 0 with get, set

type WithIntKeyMap() =
    member val Table: Map<int, string> = Map.empty with get, set

type WithSet() =
    member val Tags: Set<string> = Set.empty with get, set
    member val Trailing = 0 with get, set

/// An immutable class that is not a record: its get-only properties must stay out of the document.
type ImmutableClass(name: string, amount: int) =
    new() = ImmutableClass(null, 0)
    member this.Name = name
    member this.Amount = amount
