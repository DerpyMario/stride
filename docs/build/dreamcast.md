# Dreamcast

Stride builds for the SEGA Dreamcast as a cross-compilation target. **Nothing it produces can be
run yet** — see [What is missing](#what-is-missing). What exists today is the platform itself: the
whole runtime compiles under `STRIDE_PLATFORM_DREAMCAST`, with the console's constraints reflected
in the build rather than papered over.

## Building

```sh
dotnet build build/Stride.Runtime.slnf -p:StridePlatforms=Dreamcast
```

That builds all 38 runtime assemblies. Naming Dreamcast in `StridePlatforms` makes the whole
invocation a Dreamcast build — unlike Android or iOS, it is not something you add alongside a host
platform, because it is not a second target framework but a different meaning for the same one.

Output is isolated under `bin/Dreamcast/` and `obj/Dreamcast/`, so a Dreamcast build and a desktop
build can share a checkout without overwriting each other.

## How the platform is selected

Dreamcast has no target framework of its own (there is no `net10.0-dreamcast`) and no runtime
identifier, so neither can identify it. It compiles on the plain `$(StrideFramework)` moniker —
`net10.0`, the same one desktop uses — and is told apart by `$(StridePlatform)`, which
`Stride.Platform.props` derives from `$(StridePlatforms)`.

Everything else follows from that property:

| Property | Value | Why |
|----------|-------|-----|
| `StridePlatform` | `Dreamcast` | Derived from `StridePlatforms`; not detectable from TFM or RID |
| `DefineConstants` | `STRIDE_PLATFORM_DREAMCAST` | Replaces `STRIDE_PLATFORM_DESKTOP`, which it would otherwise inherit from the shared TFM |
| `StrideGraphicsApi` | `Null` | PowerVR2 is fixed-function; see below |
| `StrideGraphicsApiDependent` | `false` | One API, so no per-API inner builds |
| `StrideUI` | *(empty)* | No window manager, so no SDL |
| `StridePlatformDeps` | `Dreamcast` | No SH-4 natives are checked in, so the deps globs are meant to find nothing rather than to find x64 ones |
| `Platform.Type` | `PlatformType.Dreamcast` | A compile-time constant: the BCL has no `OperatingSystem.IsDreamcast()` to ask |

Because `STRIDE_PLATFORM_DESKTOP` is *replaced* rather than added to, code guarded by it — the
directory watcher, Win32 console handles, `NativeLibrary` probing, temporary directories — is
excluded. Every one of those sites already had a non-desktop branch for Android and iOS, so
Dreamcast takes the same path.

Three implementations that read as desktop-only are really the portable fallback, and now also
cover Dreamcast: the ImageSharp decoder in `StandardImageHelper.Desktop.cs`, and the in-engine text
editor in `EditText.Direct.cs` / `EditText.Direct.Default.cs`. None of them calls into an OS.

## Hardware, and what it implies

| | |
|---|---|
| CPU | Hitachi SH-4, 200 MHz |
| Main RAM | 16 MB |
| GPU | NEC PowerVR2 (CLX2), tile-based deferred |
| Video RAM | 8 MB |
| Sound | Yamaha AICA, 2 MB audio RAM |
| Media | GD-ROM, ~1 GB |
| Peripherals | Maple bus (controller, VMU, mouse, keyboard, lightgun) |

The PowerVR2 has **no programmable shader units**. Stride's renderer is shader-driven end to end,
so no existing backend can drive this GPU, and a PowerVR2 backend would have to emulate the
engine's shading model on fixed-function hardware rather than translate to it. Until such a backend
exists, Dreamcast builds select `GraphicsPlatform.Null`: the engine runs its frame loop and draws
nothing. Texture assets compile to uncompressed `R8G8B8A8` — PowerVR2 knows nothing of BCn, ETC or
ASTC, and its own VQ compression has no encoder here.

Input is unimplemented for the same reason: peripherals hang off the Maple bus, which needs a host
layer that does not exist. `InputSourceFactory` returns `null` for a Dreamcast context, so a game
gets no input rather than failing to start.

## What is missing

Two things stand between this target and a Dreamcast game, and neither is a small piece of work:

1. **A .NET runtime for SH-4.** There is none. CoreCLR and NativeAOT target x64, arm64, arm32,
   riscv64 and loongarch64 — SH-4 is not among them, and no Mono port exists either. Without one,
   a Dreamcast build produces managed assemblies with nothing to execute them.
2. **A PowerVR2 graphics backend.** `sources/engine/Stride.Graphics/Null/` is the documented
   starting point for a new backend (see `NullHelper`); a PowerVR2 one would sit beside it. The
   hard part is not the backend's shape but the shading model, as above.

Beyond those, 16 MB of main RAM is far below what the engine currently assumes, so a usable port
would also need work on the content pipeline and runtime allocation.

Consequently the Dreamcast solution platform is registered with `IsAvailable = false`: Game Studio
knows the platform but will not offer it as a build target.

## Known limitations

- The prebuilt assembly processor in `deps/AssemblyProcessor` predates `PlatformType.Dreamcast`, so
  `Stride.AssemblyProcessor.targets` passes it `--platform=Shared`. This is exact rather than a
  stand-in — the only platform that processor branches on is iOS — and the mapping can be dropped
  once `deps/AssemblyProcessor` is regenerated from `build/Stride.AssemblyProcessor.slnx`.
- Effect compilation is not supported for `GraphicsPlatform.Null`, on Dreamcast or anywhere else,
  so a Dreamcast build compiles the engine but not a game's shaders.
