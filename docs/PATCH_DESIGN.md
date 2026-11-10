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

## Runtime DLL loading: proposed, not implemented

The inspected L-Connect service is an x64 .NET Framework 4.8 managed application. .NET Framework supports loading a custom `AppDomainManager` assembly when the default application domain starts, using application configuration or process-scoped startup settings. See the [Microsoft configuration reference](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/file-schema/runtime/appdomainmanagerassembly-element).

An isolated .NET Framework console probe successfully loaded a separate manager DLL before `Main` in a 64-bit process. This confirms the generic startup-loading mechanism; it does not establish successful runtime patching of L-Connect.

A future loader could apply the PID recognition changes and OpenRGB guard in memory while keeping vendor binaries on disk unchanged. It would need to run before device discovery, handle static initialization correctly, and retain code-identity checks. Settings would be limited to the intended L-Connect processes.

This repository currently implements offline inspection and copy generation only. A runtime compatibility loader has not been implemented or tested inside L-Connect. Existing working installations continue to use the previously verified file patches.
