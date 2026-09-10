// Copyright (c) Wojciech Figat. All rights reserved.

#include "Editor/Cooker/GameCooker.h"
#include "Engine/Engine/Globals.h"
#include "Engine/Platform/File.h"
#include "Engine/Platform/FileSystem.h"
#include <ThirdParty/catch2/catch.hpp>

TEST_CASE("GameCooker")
{
    SECTION("Test IsManagedCodeFile")
    {
        // Managed code recognized by name (engine assembly) and by its image (FSharp.Core has no name rule)
        CHECK(GameCooker::IsManagedCodeFile(Globals::BinariesFolder / TEXT("FlaxEngine.CSharp.dll")));
        CHECK(GameCooker::IsManagedCodeFile(Globals::BinariesFolder / TEXT("FSharp.Core.dll")));

        // Native executable
        CHECK(!GameCooker::IsManagedCodeFile(Globals::BinariesFolder / TEXT("FlaxTests.exe")));

        const String dir = Globals::TemporaryFolder / TEXT("TestGameCooker");
        FileSystem::CreateDirectory(dir);
        const char text[] = "not an executable";

        // Files that are not valid images
        File::WriteAllBytes(dir / TEXT("Notes.txt"), text, sizeof(text) - 1);
        CHECK(!GameCooker::IsManagedCodeFile(dir / TEXT("Notes.txt")));
        const byte truncated[] = { 'M', 'Z', 0, 0 };
        File::WriteAllBytes(dir / TEXT("Truncated.dll"), truncated, sizeof(truncated));
        CHECK(!GameCooker::IsManagedCodeFile(dir / TEXT("Truncated.dll")));
        CHECK(!GameCooker::IsManagedCodeFile(dir / TEXT("Missing.dll")));

        // Symbols and docs follow their assembly
        FileSystem::CopyFile(dir / TEXT("Managed.dll"), Globals::BinariesFolder / TEXT("FSharp.Core.dll"));
        File::WriteAllBytes(dir / TEXT("Managed.pdb"), text, sizeof(text) - 1);
        File::WriteAllBytes(dir / TEXT("Managed.xml"), text, sizeof(text) - 1);
        CHECK(GameCooker::IsManagedCodeFile(dir / TEXT("Managed.pdb")));
        CHECK(GameCooker::IsManagedCodeFile(dir / TEXT("Managed.xml")));
        FileSystem::CopyFile(dir / TEXT("Native.dll"), Globals::BinariesFolder / TEXT("FlaxTests.exe"));
        File::WriteAllBytes(dir / TEXT("Native.pdb"), text, sizeof(text) - 1);
        CHECK(!GameCooker::IsManagedCodeFile(dir / TEXT("Native.pdb")));

        FileSystem::DeleteDirectory(dir);
    }
}
