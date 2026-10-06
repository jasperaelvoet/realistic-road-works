# Building Realistic Road Works

The mod is one C# assembly (`RealisticRoadWorks.dll`, .NET Framework 4.8, C# 9) plus a hand-written UI module
(`UI/RealisticRoadWorks.mjs`) that needs no build step. You can build it in two ways:

- **A. The official Cities: Skylines II modding toolchain** (Windows). It builds, post-processes and deploys to your local mods
  folder, and it can publish to Paradox Mods.
- **B. A plain `dotnet build` against the game's libraries** (Windows, Linux or macOS). It builds the DLL; you copy the files yourself.

Both use the same `RealisticRoadWorks.csproj`.

## Requirements

- **.NET SDK 8** (or newer). The project targets `net48`, which the SDK builds without Visual Studio.
- **Cities: Skylines II installed.** The game's `Cities2_Data/Managed` folder provides every reference (`Game.dll`, `Colossal.*`,
  `Unity.*`). No game files are part of this repository.
- For route A only: the modding toolchain, installed from the game (Options > Modding).

## A. Official modding toolchain

When the toolchain is installed, the game sets a few user environment variables, among them `CSII_TOOLPATH` (the folder holding the
toolchain's `Mod.props` and `Mod.targets`), `CSII_MANAGEDPATH` and `CSII_LOCALMODSPATH`. The project imports `Mod.props` and
`Mod.targets` from `CSII_TOOLPATH` whenever that variable is set, and then:

- `Mod.props` finds the game's `Managed` folder and resolves the references from it;
- `Mod.targets` runs the toolchain's post-processor after the build and copies the build output to
  `%CSII_LOCALMODSPATH%\RealisticRoadWorks` (normally
  `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\RealisticRoadWorks`).

Build from a terminal opened after the toolchain was installed, so it sees the new variables:

```
dotnet build RealisticRoadWorks.csproj -c Release
```

or open the project in Visual Studio or Rider and build the Release configuration.

**Copy the UI module.** `RealisticRoadWorks.mjs` is not compiled. To have every build put it next to the DLL (and the toolchain
deploy it with the rest of the output), add this to the csproj:

```xml
<ItemGroup>
  <None Include="UI/RealisticRoadWorks.mjs" Link="RealisticRoadWorks.mjs" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

## B. Plain `dotnet build`

Without `CSII_TOOLPATH`, the project references the game's DLLs directly from a folder you pass as `GameManagedDir`:

```
dotnet build RealisticRoadWorks.csproj -c Release -p:GameManagedDir="<game folder>/Cities2_Data/Managed"
```

Typical game folders:

| Platform | Game folder |
|---|---|
| Steam on Windows | `C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II` |
| Steam on Linux | `~/.local/share/Steam/steamapps/common/Cities Skylines II` |
| Xbox / PC Game Pass | `C:\XboxGames\Cities- Skylines II - PC Edition\Content` |

**Why `FrameworkPathOverride`.** When `GameManagedDir` is set, the project also sets `FrameworkPathOverride` to that folder, so the
compiler builds against the game's own (Unity) core libraries instead of the .NET Framework 4.8 reference assemblies. Two reasons:

- The Unity core libraries define `Span<T>` and `ReadOnlySpan<T>`, which the net48 reference assemblies lack. Several
  `Unity.Entities` overloads (for example `EntityManager.CreateEntity(archetype)`) need them to compile.
- No .NET Framework targeting pack is needed, so the same command works on Linux and macOS.

This route skips the toolchain's post-processing step. The mod does not need it: it uses no Burst-compiled jobs and no Entities
source generators.

## Release and DevTools builds

| Build | Command flag | What is compiled |
|---|---|---|
| Release (default) | none, or `-p:DevTools=false` | The product: `Mod.cs`, `RegisterSystemAttribute.cs` and `Src/` except `Src/Dev` |
| DevTools | `-p:DevTools=true` | Also `Dev/` (the dev command harness) and `Src/Dev`, and defines `DEVTOOLS`, which turns on every module's `#if DEVTOOLS` diagnostics and the `rrw.*` commands |

Release builds are what players get. A DevTools build behaves the same in game, but it also reads dev commands from a file and adds
self-checks and diagnostics. See [DEV-COMMANDS.md](DEV-COMMANDS.md).

Example DevTools build with route B:

```
dotnet build RealisticRoadWorks.csproj -c Release -p:GameManagedDir="<game folder>/Cities2_Data/Managed" -p:DevTools=true
```

Every change has to compile in both builds. Code under `Src/` may reference the dev harness only inside `#if DEVTOOLS`.

## Outputs

| Route | Output |
|---|---|
| A | `bin/Release/net48/RealisticRoadWorks.dll` (and `RealisticRoadWorks.mjs` with the UI module item above), deployed to `%CSII_LOCALMODSPATH%\RealisticRoadWorks\` |
| B | `bin/Release/net48/RealisticRoadWorks.dll` (and `RealisticRoadWorks.mjs` with the UI module item above) |

The game references are never copied to the output (`Private=false`).

## Installing a build

Put two files in the game's local mods folder, in a folder named `RealisticRoadWorks`:

```
Mods/RealisticRoadWorks/RealisticRoadWorks.dll
Mods/RealisticRoadWorks/RealisticRoadWorks.mjs
```

The `Mods` folder is in the game's user data folder:

- Windows: `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods`
- Steam on Linux (Proton): `steamapps/compatdata/949230/pfx/drive_c/users/steamuser/AppData/LocalLow/Colossal Order/Cities Skylines II/Mods`

Then restart the game. Mods are loaded only at startup. The mod logs to `Logs/RealisticRoadWorks.log` in the same user data folder.
At startup the log lists every registered system. A failed registration shows up there as an error line.

## Publishing to Paradox Mods

Publishing uses route A. The toolchain's mod publisher reads `Properties/PublishConfiguration.xml` (set in the csproj as
`PublishConfigurationPath`) and uploads the contents of the deploy folder.

1. Fill in `Properties/PublishConfiguration.xml`. `Properties/Thumbnail.png` and the screenshots in `Properties/Screenshots/` are in the repository; replace them if you like. Set `ModVersion` and
   `ChangeLog` for the release. Leave `ModId` empty for the first upload.
2. Make a Release build (not DevTools) and make sure the deploy folder holds both `RealisticRoadWorks.dll` and
   `RealisticRoadWorks.mjs`.
3. Publish with one of the profiles in `Properties/PublishProfiles/`:

   | Profile | Use |
   |---|---|
   | `PublishNewMod` | First upload. Afterwards, write the returned mod id into `ModId`. |
   | `PublishNewVersion` | A new version of the published mod (needs `ModId`). |
   | `UpdatePublishedConfiguration` | Update the description, screenshots, tags and so on, without uploading a new build. |

   ```
   dotnet publish RealisticRoadWorks.csproj -c Release /p:PublishProfile=PublishNewVersion
   ```

   or use Publish in Visual Studio or Rider and pick the profile.

The publisher uploads with the Paradox account the game is logged in to, so log in from the game once before you publish.
