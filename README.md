# TURZX 8.8-inch to L-Connect

An unofficial compatibility layer that lets a TURZX 8.8-inch USB display use the **Universal Screen 8.8 Inch** path in L-Connect 3. The runtime loader automatically applies compatibility hooks in memory while retaining the original vendor DLLs and service executable on disk. An offline inspection and copy-generation CLI is also available.

The verified combination is **L-Connect 3 2.1.29.0 / lcd207 SDK 1.4.0.146 / TURZX `1CBE:0088`**. Display themes, live sensor values, OpenRGB initialization, and lighting application were verified on real hardware. Compatibility with every future release is not guaranteed: the tool rejects files whose relevant code structure or protocol fingerprint differs from the approved baseline.

## What it changes

| Original assembly | In-memory behavior |
| --- | --- |
| `L-Connect.Core.dll` | Changes the Universal Screen registration PID from `A088` to `0088`. |
| `lianli.lcd207.dll` | Changes the SDK's `LCD_88inchPid` from `A088` to `0088`. |
| `L-Connect-Service.exe` | Skips displays without a separate RGB controller in the OpenRGB initialization callback. |

The display's firmware and actual USB identifiers are unchanged. The runtime loads the installed vendor assemblies and hooks their managed code. The offline CLI inspects metadata without executing those assemblies.

## Runtime loader

`Turzx.LConnect.Bootstrap` uses the .NET Framework application-domain manager mechanism in the service and UI application configurations. It waits for the CLR to load the entry assembly, then initializes `Turzx.LConnect.Runtime` synchronously before `Main`. Only these two intended processes are supported; no global startup environment variables are needed.

The runtime validates the PID sites, USB protocol/key, OpenRGB callback, and data-initializer graph before installing Harmony hooks. It rebuilds both forward and reverse device mappings and skips the display's absent RGB controller. Changed or already file-patched vendor inputs are rejected. Partial initialization removes installed hooks and restores original in-memory registries; incomplete rollback prevents normal startup.

Startup integration adds our own bootstrap DLL and two application-configuration entries. Vendor DLLs and the service executable remain original. Local maintenance restores the startup integration after a compatible vendor update; an unknown protocol or code layout requires review. Installation and maintenance scripts are deliberately outside the public source tree.

Live validation on 2026-11-10, including a full Windows reboot, confirmed automatic service/UI loading, original vendor file hashes, theme playback, sensor frames, successful OpenRGB lighting application, and all four RAM controllers. An actual future L-Connect update has not yet been tested.

## Build

Verified on Windows with the .NET 10 SDK. Mono.Cecil 0.11.6 is restored from NuGet and pinned by the package reference and lock file.

The loader and test host target .NET Framework 4.8; Harmony 2.4.3 and Mono.Cecil 0.11.6 are pinned. Build the loader and its test host with the .NET Framework 4.8 targeting pack installed:

```powershell
dotnet restore tests/Turzx.LConnect.Runtime.TestHost/Turzx.LConnect.Runtime.TestHost.csproj --locked-mode
dotnet build tests/Turzx.LConnect.Runtime.TestHost/Turzx.LConnect.Runtime.TestHost.csproj --no-restore -c Release
```

The test host exercises real original assemblies without opening a USB device. Its startup-manager configuration and process-local `TURZX_TEST_*` settings must point to owned original files, installed dependencies, runtime DLLs, and a private log directory. See [the validation notes](docs/PATCH_DESIGN.md#runtime-loader-validation) for the tested conditions.

Build the offline CLI separately:

```powershell
dotnet restore src/LianLiTurzxCompat/LianLiTurzxCompat.csproj --locked-mode
dotnet build src/LianLiTurzxCompat/LianLiTurzxCompat.csproj --no-restore -c Release
```

## Inspect an installation

```powershell
dotnet run --project src/LianLiTurzxCompat -c Release --no-build -- inspect --input "C:\Program Files\Lian-Li\L-Connect 3"
```

The input directory must contain all three files listed above. `Native` means the known unpatched code structure; `Patched` means the corresponding change is already present. The JSON report includes input and planned-output SHA-256 hashes, assembly versions, fingerprints, and change locations. Binary payloads are not included in the report.

## Prepare patched copies

```powershell
dotnet run --project src/LianLiTurzxCompat -c Release --no-build -- prepare --input "C:\Program Files\Lian-Li\L-Connect 3" --output ".local\prepared"
```

The output must be a new directory outside both the input and dependency directories. All three files are checked before output is created. The tool writes three copies and a `manifest.json`; already-patched inputs can also be copied without adding another patch. A failed write may leave partial output, but the input files are never written.

If you keep the original files in a separate directory, provide the installed dependency directory explicitly:

```powershell
dotnet run --project src/LianLiTurzxCompat -c Release --no-build -- inspect --input "D:\OwnedLConnectFiles" --dependencies "C:\Program Files\Lian-Li\L-Connect 3"
```

## Repository scope

This repository contains the runtime loader, test host, offline patch engines and CLI, and design and validation notes. It does not include Lian Li executables, DLLs, theme assets, generated patches, device serial numbers, personal PC settings, or runtime logs. Third-party binaries are restored locally rather than committed.

Installation, deployment, service-restart, scheduled-task, and automatic-update monitoring scripts are maintained separately as local operational files and are excluded from Git. This CLI does not replace installed files, control services, or modify scheduled tasks.

See [the design and validation notes](docs/PATCH_DESIGN.md) for the patch locations, fingerprint checks, runtime architecture, and verified behavior.
