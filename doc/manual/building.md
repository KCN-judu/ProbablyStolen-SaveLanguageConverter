# Building


## Requirements

- *Probably Stolen*, game version 0.46D. The build compiles against the game's own assemblies in `Probably Stolen_Data/Managed`.
- Git Bash or another POSIX shell on Windows.
- Nothing else: the C# 5 compiler that ships with Windows (.NET Framework 4) is enough.

## Build

```bash
./build.sh            # out/save_language_converter
./build.sh --install  # also copies it to the game's Mods folder
```

Two environment variables override the defaults:

| Variable | Default |
| --- | --- |
| `GAME` | `/d/SteamLibrary/steamapps/common/Probably Stolen` |
| `CSC` | `/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe` |

## Checks

```bash
tests/run.sh
```

It runs the engine's checks headless, and the game's mod scanner on the build when the game is installed.

## Install by hand

Copy the folder `out/save_language_converter` into the game's `Mods` folder, start the game, and enable the mod in the Mods menu. Mods load when the game starts, so restart after enabling or updating one.

The mod declares no capabilities, so the game enables it without a permission prompt.

## Regenerating the type list

`src/KnownTypes.g.cs` lists the save classes the walker visits. Regenerate it with `tools/Closure.cs` after a game update that changes save classes.
