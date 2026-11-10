# TURZX 8.8-inch to L-Connect

An unofficial compatibility patcher that lets a TURZX 8.8-inch USB display use the **Universal Screen 8.8 Inch** path in L-Connect 3. It inspects an existing installation and generates patched copies in a separate directory when the code matches the approved baseline.

The verified combination is **L-Connect 3 2.1.29.0 / lcd207 SDK 1.4.0.146 / TURZX `1CBE:0088`**. Display themes, live sensor values, OpenRGB initialization, and lighting application were verified on real hardware. Compatibility with every future release is not guaranteed: the tool rejects files whose relevant code structure or protocol fingerprint differs from the approved baseline.

## What it changes

| File | Change |
| --- | --- |
| `L-Connect.Core.dll` | Changes the Universal Screen registration PID from `A088` to `0088`. |
| `lianli.lcd207.dll` | Changes the SDK's `LCD_88inchPid` from `A088` to `0088`. |
| `L-Connect-Service.exe` | Skips displays without a separate RGB controller in the OpenRGB initialization callback. |

The tool does not change the display's firmware or actual USB identifiers. Target assemblies are inspected as metadata and are not executed.

## Build

Verified on Windows with the .NET 10 SDK. Mono.Cecil 0.11.6 is restored from NuGet and pinned by the package reference and lock file.

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

This repository contains the patch engines, the inspection/copy-generation CLI, and design and validation notes. It does not include Lian Li executables, DLLs, theme assets, generated patches, device serial numbers, personal PC settings, or runtime logs. Mono.Cecil binaries are restored locally rather than committed.

Installation, deployment, service-restart, scheduled-task, and automatic-update monitoring scripts are maintained separately as local operational files and are excluded from Git. This CLI does not replace installed files, control services, or modify scheduled tasks.

See [the design and validation notes](docs/PATCH_DESIGN.md) for the patch locations, fingerprint checks, verified behavior, and the proposed runtime-loading alternative.
