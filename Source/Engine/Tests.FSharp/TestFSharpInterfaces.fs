// Copyright (c) Wojciech Figat. All rights reserved.

namespace FlaxEngine.Tests.FSharp

open System.Reflection
open FlaxEngine.Networking
open NUnit.Framework

/// An F# implementation of an engine (native) scripting interface.
type NetworkSerializableFSharp() =
    interface INetworkSerializable with
        member _.Serialize(_stream: NetworkStream) = ()
        member _.Deserialize(_stream: NetworkStream) = ()

/// Tests for how F# implements engine interfaces, which the script vtable of a managed type relies on to route native
/// interface calls to F# code (BinaryModule.cpp, FindMethod; the native call itself is covered by the engine's
/// TestScripting "Test Interface" with an explicit C# implementation, which compiles to the same shape).
[<TestFixture>]
type TestFSharpInterfaces() =

    /// Test that F# implements an interface with a non-public instance method named after the interface - not with a method
    /// named like the interface method - and that this is the name the engine looks for.
    [<Test>]
    member _.TestInterfaceImplementationIsExplicit() =
        let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly
        let methods = typeof<NetworkSerializableFSharp>.GetMethods(flags)
        let expectedName = typeof<INetworkSerializable>.FullName + ".Serialize"

        Assert.IsFalse(methods |> Array.exists (fun m -> m.Name = "Serialize"), "F# implements interfaces explicitly, so no method is named like the interface one")
        let serialize = methods |> Array.tryFind (fun m -> m.Name = expectedName)
        Assert.IsTrue(serialize.IsSome, "no method named " + expectedName + " in: " + (methods |> Array.map (fun m -> m.Name) |> String.concat ", "))
        Assert.IsFalse(serialize.Value.IsPublic, "the implementation is not public")
        Assert.IsFalse(serialize.Value.IsStatic)
