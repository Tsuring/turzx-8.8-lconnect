# TURZX 8.8-inch to L-Connect

Use a TURZX 8.8-inch USB display through the **Universal Screen 8.8 Inch** feature in L-Connect 3, including display themes and live CPU/GPU sensors.

**v0.2.0 uses automatic, in-memory compatibility hooks.** The original Lian Li DLLs and service executable stay unchanged on disk. The display firmware and USB identifiers also stay unchanged. This is an unofficial project and is not affiliated with Lian Li or TURZX.

[Download v0.2.0](https://github.com/Tsuring/turzx-8.8-lconnect/releases/tag/v0.2.0) · [Design and validation](docs/PATCH_DESIGN.md)

## Verified compatibility

| Component | Verified version or mode |
| --- | --- |
| L-Connect 3 | 2.1.29.0, Windows x64 |
| lcd207 SDK | 1.4.0.146 |
| TURZX display | 8.8-inch, normal USB mode, `1CBE:0088` |
| Runtime | .NET Framework 4.8 |

The secondary-screen USB display mode is not the target of this loader. Other display models, firmware revisions, and future L-Connect releases are unverified. Compatibility checks reject unknown relevant code or protocol fingerprints.

## Verified after a full Windows reboot

- L-Connect service and UI started and loaded the compatibility DLL automatically.
- The display was recognized through the Universal Screen path and its selected theme was restored.
- HWiNFO initialized; CPU/GPU values reached rendered display frames. The owner confirmed the physical theme and updating sensor values.
- OpenRGB detected four RAM modules, the GPU, and motherboard. All ten entries in L-Connect's combined list were available, and configured lighting applied successfully.
- Original vendor file hashes remained unchanged, and the TURZX monitor process stayed stopped.

These results were verified on 2026-11-10. An actual future L-Connect update has not yet been tested.

## Download contents

`turzx-lconnect-runtime-v0.2.0-win-x64.zip` contains the two compatibility DLLs, pinned Harmony/Cecil dependencies, this README, design notes, dependency notices, and a per-file SHA-256 manifest. A separate `SHA256SUMS.txt` verifies the archive.

This is a **component package with manual startup integration**, not an installer. It contains no vendor DLLs, patched vendor files, firmware, theme assets, test-host executables, installation scripts, or scheduled-task/update-monitoring scripts.

## Configure automatic loading

Administrator access is required to change the application directory. Keep byte-exact backups of the two existing application configurations before editing them.

1. Use the display's normal USB mode. Disable the TURZX monitor's startup/watchdog integration and close that program so it does not compete for the device.
2. Close the L-Connect UI and stop its watcher and service. If an earlier file-replacement patch is installed, restore the original `L-Connect.Core.dll`, `lianli.lcd207.dll`, and `L-Connect-Service.exe` from your own matching installation or verified backups. The memory loader rejects already file-patched inputs.
3. Copy all DLLs from the archive's `runtime` directory to `%ProgramData%\Lian-Li\TURZX-Compatibility\runtime`. Keep this directory writable only by administrators and SYSTEM; normal users need read/execute access.
4. Copy `Turzx.LConnect.Bootstrap.dll` from that directory beside the L-Connect executables, usually in `C:\Program Files\Lian-Li\L-Connect 3`.
5. In **both** `L-Connect-Service.exe.config` and `L-Connect 3.exe.config`, merge the following two elements into the existing `<runtime>` element. Preserve the existing settings and assembly binding redirects. If another application-domain manager is already configured, stop and resolve that conflict rather than replacing it.

```xml
<appDomainManagerAssembly value="Turzx.LConnect.Bootstrap, Version=0.2.0.0, Culture=neutral, PublicKeyToken=null" />
<appDomainManagerType value="Turzx.LConnect.Bootstrap.CompatibilityDomainManager" />
```

6. Start the L-Connect service/watcher and UI again. Keep L-Connect's normal automatic-service and logon startup enabled if you want it to start with Windows.

Once configured, the CLR automatically loads the bootstrap on each service/UI launch. No separate injector or system-wide startup environment variables are needed. Per-process diagnostics are written under `%ProgramData%\Lian-Li\TURZX-Compatibility\logs`; `RuntimeReady` identifies successful hook initialization. The service log should also show the display SDK binding, theme application, and successful sensor/OpenRGB initialization.

For rollback, stop L-Connect, restore the two configuration backups, and start it again. The vendor DLLs and executable do not need repatching. The display has no separate RGB controller, so unsupported display lighting-firmware/effect messages can still appear; they are distinct from successful OpenRGB initialization for the other devices.

## L-Connect updates

Startup/update maintenance was verified on the development PC, but its machine-specific scripts are not part of this release. The downloadable package does not install automatic update repair. If a vendor update resets the startup entries or removes the bootstrap, review the new version and restore the integration only after its compatibility checks pass. Future release compatibility is not guaranteed.

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

Startup integration adds our own bootstrap DLL and the application-configuration entries above. Vendor DLLs and the service executable remain original. The code preserves the hosts' net48 compatibility behavior so early loading does not break HWiNFO XML parsing.

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

## Offline CLI: inspect an installation

```powershell
dotnet run --project src/LianLiTurzxCompat -c Release --no-build -- inspect --input "C:\Program Files\Lian-Li\L-Connect 3"
```

The input directory must contain all three files listed above. `Native` means the known unpatched code structure; `Patched` means the corresponding change is already present. The JSON report includes input and planned-output SHA-256 hashes, assembly versions, fingerprints, and change locations. Binary payloads are not included in the report.

## Offline CLI: prepare patched copies

The offline CLI is a separate advanced workflow. Its generated vendor copies are incompatible with the runtime loader's original-input requirement; use one approach at a time.

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
