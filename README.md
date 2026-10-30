# Renaissance

Universal VST2/VST3 legacy plugin bridge (see `plan.md`). Host old 32-bit and 64-bit VST2 plugins inside any DAW,
with crash isolation, modern preset management and a better generic parameter editor.

```
src/
  Renaissance.Core            business logic: IPC, host client, PluginSession, audio bridge, WrapperLink (DAW side)
  Renaissance.PresetEngine    FXP / FXB / .rpreset + preset store
  Renaissance.PluginDatabase  SQLite (Plugins, ScanLocations, PluginMetadata, CrashHistory)
  Renaissance.Scanner         scans via isolated hosts only
  Renaissance.Host64/Host32   isolated VST2 host processes (Host32 compiles Host64's sources as x86)
  Renaissance.Vst2            native VST2 plugin DLL (NativeAOT), thin wrapper over WrapperLink
  Renaissance.Vst3            native VST3 plugin module (NativeAOT), thin wrapper over WrapperLink
  Renaissance.UI              Avalonia app: Scan View + Plugin View + parameter dialog with dials
tests/Renaissance.Tests
docs/UI-DESIGN.md             how to adjust colours, fonts, layout and DAW window size
```

## How it works in a DAW

```
DAW -> Renaissance.dll / Renaissance.vst3 (native) --shared memory--> Host32/Host64 -> your VST2 plugin
              |                                                          ^
              +-- control pipe --> Renaissance.exe (embedded UI, owns the session) --+
```

The wrapper launches the Renaissance app embedded in the DAW's editor window. The app owns the plugin session and
its isolated host process. Audio, MIDI and automation go straight from the wrapper to the host through shared memory.
If a plugin crashes only its host process dies, the session restarts it and restores state.

## Build and test

```powershell
dotnet build Renaissance.slnx
dotnet test                                   # unit + integration tests, incl. simulated VST2 and VST3 DAWs
pwsh ./scripts/build-vst2.ps1                         # builds the plugins into artifacts\Renaissance-VST2
pwsh ./scripts/build-vst2.ps1 -Install "C:\VSTPlugins\Renaissance"
```

Close your DAW before rebuilding: it locks the plugin file. First launch after a rebuild can take ~20 seconds
(Windows Defender scanning the new files); excluding the folder avoids that.

## Installing

`artifacts\Renaissance-VST2` contains:

| File | For |
|---|---|
| `Renaissance.dll` | VST2 **instrument** (Reason 10 and other VST2 hosts) |
| `Renaissance FX.dll` | VST2 **effect** |
| `Renaissance.vst3\` | VST3 bundle with an instrument **and** an effect class (Reason 14, Cubase, Live, Reaper, ...) |
| `App\` | the Renaissance app and plugin hosts; must stay next to the plugins |

* **VST2:** add the folder to your DAW's VST2 plugin paths and rescan.
* **VST3:** copy `Renaissance.vst3` to `C:\Program Files\Common Files\VST3` (or run
  `pwsh ./build-vst2.ps1 -InstallVst3` from an administrator PowerShell, which also records where `App` lives in
  `%LOCALAPPDATA%\Renaissance\app-path.txt`), then rescan.

Logs: `%LOCALAPPDATA%\Renaissance\logs` (`vst2.log`, `vst3.log`, `wrapper.log`, `renaissance-*.log`).

## Status

Verified by automated tests against the real 32-bit Native Instruments B4 II: scanning through Host32, loading,
FXP/state round-trips, crash recovery, and full audio chains driven through the real VST2 DLL and VST3 module by
simulated hosts (state load, MIDI in, audio out, state save, clean shutdown).

Not done / known limits:
- Only tested in simulated hosts plus Reason 10 (VST2). The VST3 module has not yet been tried in a real DAW.
- DAW automation exposes 128 generic parameters (P1..P128); plugin parameter names are not mirrored.
- VST2 SysEx and VST3 SysEx events are not forwarded; tempo/transport are not passed to the hosted plugin.
- Keyboard typing inside the embedded window may be captured by the DAW.
- Mono and surround layouts: the wrappers present stereo.
