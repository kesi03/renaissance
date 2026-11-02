# Renaissance

![Logo](docs/screenshots/top.png)

> Universal VST2/VST3 legacy plugin bridge

Host legacy 32-bit and 64-bit VST2 plugins inside modern DAWs with:

- Crash isolation
- Modern preset management
- Generic parameter editor
- VST2 , VST3 and Clap wrappers
- Embedded Avalonia UI
- NativeAOT wrappers
- Automated plugin scanning


---

## Screenshot Gallery

> Replace these placeholder images as development progresses.

### Main Plugin Window

![Main Window](docs/screenshots/reason-14-plugins-scan.png)

### Plugin Folder

![Plugin Folder](docs/screenshots/add-a-folder-to-scan.png)

### Save Patches

![Preset Save](docs/screenshots/save-patches.png)

### Generic Parameter Editor

![Parameter Editor](docs/screenshots/reason-14-plugin-parameters.png)

### Show the plugin

![Parameter Editor](docs/screenshots/reason-14-plugin-show.png) 


---

## Solution Layout

```text
src/
  Renaissance.Core
    Business logic:
      IPC
      Host client
      PluginSession
      Audio bridge
      WrapperLink

  Renaissance.PresetEngine
    FXP / FXB / .rpreset support
    Preset storage

  Renaissance.PluginDatabase
    SQLite database:
      Plugins
      ScanLocations
      PluginMetadata
      CrashHistory

  Renaissance.Scanner
    Plugin scanning via isolated hosts

  Renaissance.Host64
  Renaissance.Host32
    Isolated VST2 host processes

  Renaissance.Vst2
    NativeAOT VST2 wrapper

  Renaissance.Vst3
    NativeAOT VST3 wrapper

  Renaissance.UI
    Avalonia desktop application

tests/
  Renaissance.Tests

docs/
  UI-DESIGN.md
```

# Tested on

- Reason 10
- Reason 14
- Morning Daw