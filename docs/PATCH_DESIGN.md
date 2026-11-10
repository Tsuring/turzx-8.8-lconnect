# Patch design and validation

## Device identification and patch locations

In normal USB mode, the verified TURZX display uses `VID_1CBE / PID_0088`. The verified L-Connect Universal Screen path and lcd207 SDK expect `1CBE:A088`. Changing the two PC-side PID recognition sites lets the display use the existing rendering path.

The display-only mode identified as `1A86:AD11` is a separate path. This patcher targets normal USB mode and does not write device identifiers or firmware.

`PatchPlanner` does not search for a fixed file offset. It finds a unique CIL context and checks the decoded instruction against the actual PE method body:

- `KnownDevices` static constructor: the `UniversalScreen8p8Inch` enum value `17043456`, VID `1CBE`, PID, and `USBDeviceID` constructor sequence.
- `lcd207.WinUsbLed` static constructor: adjacent assignments to `LCD_88inchPid` and `LCD_88inchVid`.

Each PID patch must change exactly one byte, `A0` to `00`. Multiple candidates or changes to the expected instruction shape, VID, or enum value cause rejection.

## Protocol fingerprint

The fingerprint covers instructions, branches, local variables, exception regions, and associated static data in `GetBaseCmdBuf`, `Encrypt`, `GetVer`, `PushImage`, `SendAndRead`, and the selected sensor-rendering methods. It includes the eight-byte key data referenced by `InitializeArray`.

Verified SDK protocol fingerprint:

```text
15C949EB2AF82CC42931ED239B585FED9A0873F9D1CBD7367E3D70A2F322E595
```

This checks the identity of selected code and static data. It does not prove compatibility of the entire SDK or every external dependency. Approving a new fingerprint requires a separate code comparison and real-device validation.

## OpenRGB initialization guard

The verified service assumed that an LCD's `ledUsb` controller was present when adding the display to the OpenRGB list. A display without a separate RGB controller caused a `NullReferenceException` during initialization.

`OpenRgbGuard` preserves the existing callback and inserts this condition at its entry:

```csharp
if (o.ledUsb == null) return;
```

The normalized fingerprint of the original callback must match the approved value. An existing identical guard is recognized without inserting another one. Strong-name signed assemblies, ambiguous or changed callbacks, and new exception-handler layouts are rejected.

```text
d46467ed681b937ecd509ec251aafeb85424f71e7e5e6e29f68e24560798f124
```

## Verified behavior

- Original inputs produced the expected PID change locations and output hashes.
- Patched inputs were recognized as `Patched` with the same protocol fingerprint.
- An incorrect expected protocol fingerprint was rejected without returning file plans.
- Comparing all CIL methods before and after the OpenRGB change found exactly one modified callback.
- An isolated callback invocation with an LCD lacking an RGB controller returned without creating a USB object.
- Real L-Connect display themes and updating CPU/GPU sensor values were confirmed.
- After a reboot, four RAM RGB controllers, the GPU, and motherboard were detected again. The required L-Connect OpenRGB indexes were present and configured lighting applied successfully.

`SDK initialized` alone does not establish successful lighting application. Validation also checked missing indexes and `ApplyResult=True` / `Init success`. Earlier RAM detection failures cleared after a reboot; their exact driver or bus cause remains undetermined.

## Excluded operational material

Original and patched vendor binaries, device serial numbers, runtime records containing personal paths, original backups, and machine-specific operational scripts remain outside the tracked source or in ignored local storage. Deployed automatic-maintenance state and PC-specific settings are managed separately from these public validation results.

## CLI validation

Verified on 2026-11-10 using Windows, .NET SDK 10.0.202, and Mono.Cecil 0.11.6.

| Check | Result |
| --- | --- |
| Release build | Zero warnings and errors. |
| Inspect three original files | Three `Native` states; all planned output hashes match the previously verified patches. |
| Inspect installed patched files | Three `Patched` states; zero additional changes required. |
| Generate copies in a new output directory | All three actual SHA-256 hashes match the plan. |
| Change one byte of the SDK static key | Rejected because the protocol fingerprint changed; no output directory created. |
| Reuse an existing output directory | Rejected before writing existing contents. |
| Pass an output option to `inspect` | Rejected by argument validation. |

The CLI uses deferred Cecil metadata reading, avoiding eager resolution of unrelated .NET Framework/WPF custom attributes in a modern .NET host. Vendor binaries used for these checks are not included in this repository.

## Automatic runtime DLL loading

The inspected L-Connect service is an x64 .NET Framework 4.8 managed application. .NET Framework supports loading a custom `AppDomainManager` assembly when the default application domain starts, using application configuration or process-scoped startup settings. See the [Microsoft configuration reference](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/file-schema/runtime/appdomainmanagerassembly-element).

The implemented bootstrap has no third-party dependencies. It preserves the verified hosts' `.NETFramework,Version=v4.8` compatibility policy before loading dependencies and registers an `AssemblyLoad` handler for the entry assembly. The CLR loads that assembly itself; the handler initializes the runtime before application `Main`. Loading the entry executable manually during `InitializeNewDomain` was reproduced to cause `FileLoadException` / `E_UNEXPECTED` at startup. Allowing early dependency loading to cache legacy framework policy also caused HWiNFO XML parsing to request a missing external DTD. Both cases were corrected and checked.

`Turzx.LConnect.Runtime` validates the original inputs and uses Harmony 2.4.3 for two initializer transpilers, an OpenRGB callback prefix, and a read-only sensor-frame observation postfix. Harmony can initialize a type while preparing its hook, so the two data-only initializers are deliberately replayed after patch installation. Replaying Core rebuilds forward and reverse mappings. The SDK initializer assigns only VID/PID constants. A normalized fingerprint covers the initializer graph, local USB ID behavior, and LCD static data construction:

```text
4EC87B42798662F95B3049315A4688139389C133F5CA4FEE0D2150F0089C77DF
```

The loader refuses already patched vendor inputs. On failure it removes its hooks, attempts each registry reset independently, and checks that no owned hooks remain. Bootstrap resolver handlers are detached on failure. An incomplete rollback is treated as unsafe startup.

The protected runtime directory contains our DLLs and restored NuGet dependencies. Our bootstrap DLL also resides beside the two executables, whose application configurations select the custom manager. No vendor binary, device firmware, USB descriptor, or system-wide startup variable is changed by the runtime loader. Local startup/update maintenance validates payload hashes and repairs only our bootstrap and the two startup configurations. It waits for an update to settle and leaves unsupported vendor versions unpatched.

## Runtime loader validation

Verified on Windows on 2026-11-10 with L-Connect 3 2.1.29.0 and lcd207 SDK 1.4.0.146:

| Check | Result |
| --- | --- |
| Release build | Zero warnings and errors. |
| Isolated original-assembly startup | Hooks ready before `Main`; PID `0088` in Core and SDK; reverse lookup correct; input file hashes unchanged. |
| Original callback with an LCD without RGB | Callback skipped without opening a USB device. |
| Deliberate failure after Core replay | Hooks removed; both registries restored to original PID `A088`. |
| Framework compatibility | net48 policy retained; XML with an absent external DTD parses using the normal default resolver behavior. |
| Startup configuration fixtures | Existing settings/binding redirects preserved; repeated merge unchanged; byte-exact recovery; unrelated manager rejected. |
| Live service and UI | Both automatically loaded the compatibility code; all three vendor file hashes match originals. |
| Theme and sensors | Theme applied; a rendered sensor bitmap contained nonzero CPU/GPU readings; UI readings changed; physical display/theme and updating sensors confirmed by its owner. |
| OpenRGB | Six physical controllers including four RAM modules; L-Connect required indexes 0 through 9 all available; `ApplyResult=True` and `Init success`. |
| Startup/update maintenance | Old binary-replacement action replaced with startup-integration maintenance; post-reboot heartbeat reports Ready. An actual future vendor update remains untested. |
| Full Windows reboot | Service and UI automatically loaded the hooks; original vendor hashes retained; theme applied and nonzero sensor frame rendered; OpenRGB detected all four RAM controllers and applied lighting with no missing indexes; TURZX process absent. Owner reported normal operation. |

The six physical OpenRGB controllers comprise four RAM modules, one GPU, and one motherboard. L-Connect adds four native controller bridges, bringing its combined list to ten entries. All required indexes `0..9` were available after reboot. The original-file hash check and startup logs were collected from the new post-reboot processes, rather than reused from the earlier service restart.

An actual future L-Connect update remains outside the verified results. The local maintenance integration is configured to recheck compatibility and restore startup entries, but this does not guarantee compatibility with an untested vendor release.

The display has no separate RGB controller, so its unsupported lighting-firmware/effect messages can still appear. The runtime skips that display during OpenRGB registration; it does not manufacture lighting hardware.
