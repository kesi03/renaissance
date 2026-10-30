# Renaissance
## Universal VST2/VST3 Legacy Plugin Bridge

---

# Vision

Renaissance is a modern plugin wrapper platform designed to allow legacy VST2 plugins to run inside both older and modern DAWs while providing an enhanced user experience, improved preset management, crash isolation, plugin organisation, and a modern parameter editing system.

Unlike traditional bridges, Renaissance is intended to become a complete plugin management environment.

Primary goals:

1. Load legacy VST2 plugins in modern DAWs.
2. Support both VST2 and VST3 Renaissance wrappers.
3. Support both 32-bit and 64-bit VST2 plugins.
4. Prevent plugin crashes from crashing the DAW.
5. Provide modern preset management.
6. Provide a superior generic parameter editor.
7. Maintain a searchable plugin database.
8. Provide a modern commercial-quality UI.

---

# Supported Hosts

## Renaissance VST2

Used for:

- Reason 10
- Legacy VST2 DAWs
- Older host environments

Format:

```text
VST2
```

Architecture:

```text
x64
```

---

## Renaissance VST3

Used for:

- Reason 14
- Cubase
- Ableton Live
- Reaper
- Studio One
- FL Studio

Format:

```text
VST3
```

Architecture:

```text
x64
```

---

# Supported Plugin Types

## Legacy VST2 Plugins

### 32-bit VST2

Loaded through:

```text
Host32.exe
```

Architecture:

```text
x86
```

---

### 64-bit VST2

Loaded through:

```text
Host64.exe
```

Architecture:

```text
x64
```

---

# High-Level Architecture

```text
                    DAW

                      |
          +-----------+-----------+
          |                       |
          v                       v

   Renaissance.VST2       Renaissance.VST3
          x64                  x64

          |                     |
          +----------+----------+
                     |
                     v

             Renaissance.Core

                     |
          +----------+----------+
          |                     |
          v                     v

          Host32            Host64
            x86               x64

          |                     |

     VST2 32-bit          VST2 64-bit
```

---

# Project Structure

```text
/src

  Renaissance.Core

  Renaissance.Vst2

  Renaissance.Vst3

  Renaissance.Host32

  Renaissance.Host64

  Renaissance.Scanner

  Renaissance.PresetEngine

  Renaissance.PluginDatabase

  Renaissance.UI

/tests

  Renaissance.Tests
```

---

# Technology Stack

## Language

```text
C# .NET 10
```

---

## UI Framework

Preferred:

```text
Avalonia UI
```

Benefits:

- Modern
- Hardware accelerated
- Themeable
- Future cross-platform support

---

## Audio Hosting

```text
VST.NET
```

Libraries:

```text
Jacobi.Vst.Core
Jacobi.Vst.Host.Interop
Jacobi.Vst.Framework
```

---

## Logging

```text
Serilog
```

---

## Storage

```text
SQLite
```

---

# Core Features

---

# Plugin Loading

Must support:

```text
32-bit VST2
64-bit VST2
```

Features:

- Plugin discovery
- Plugin loading
- Plugin unloading
- Editor opening
- Editor resizing

---

# Audio Processing

Supports:

```text
Stereo
Mono
```

Future:

```text
Multi-channel
```

Uses:

```csharp
ProcessReplacing()
```

Requirements:

- Real-time safe
- Zero allocations
- No locks in audio thread

---

# MIDI Support

Incoming:

```text
Note On
Note Off
CC
Pitch Bend
Aftertouch
Program Change
SysEx
```

Outgoing:

```text
Generated MIDI
CC
Program Change
Sequencer output
Arpeggiators
```

---

# Preset Management

Supports:

```text
Plugin Programs
FXP
FXB
```

And native Renaissance format:

```text
.rpreset
```

---

## Preset Actions

- Save
- Save As
- Load
- Rename
- Delete
- Import FXP
- Export FXP
- Import FXB
- Export FXB

---

# Plugin Scanning

Supports:

- Multiple folders
- Incremental scans
- Full rescans
- Failed plugin views

Stores:

```text
Plugin Name
Version
Vendor
Architecture
Category
Path
```

---

# Plugin Metadata

Supports:

- Tags
- Favourites
- Categories
- Custom Icons

Supported image formats:

```text
PNG
SVG
ICO
```

---

# Crash Protection

All plugin loading occurs inside isolated host processes.

Benefits:

```text
Plugin Crash != DAW Crash
```

Features:

- Crash logging
- Crash history
- Automatic host restart
- Plugin state recovery

---

# Scanner Architecture

Scanner must never load plugins directly.

Flow:

```text
Scanner
    ↓
Host32/Host64
    ↓
Load Plugin
    ↓
Read Metadata
    ↓
Return Results
```

Benefits:

```text
Bad plugins cannot crash scanner
```

---

# User Interface

## Design Principles

The application uses only two primary views:

```text
1. Scan View
2. Plugin View
```

Workflow:

```text
Scan
    →
Select Plugin
    →
Load Plugin
    →
Work
```

No additional navigation systems should be required.

---

# View 1 - Scan View

Purpose:

```text
Plugin Discovery
Scanning
Diagnostics
Plugin Management
```

---

## Layout

```text
-----------------------------------------------------------------

[VST Folders                                      ] [Add] [Remove]

-----------------------------------------------------------------

[Scan] [Rescan All] [Scan Failed] [Stop]

[x] Test Editors

[x] Hide Popups During Scan

-----------------------------------------------------------------

[Search.........................................]

[Instruments ▼]

-----------------------------------------------------------------

| Name | Version | Vendor | Type | Bits | Host | Status | Details |

|---------------------------------------------------------------|

|                                                       Grid    |

-----------------------------------------------------------------
```

---

## Folder Management

Supports:

```text
C:\VSTPlugins

C:\Program Files\VSTPlugins

C:\Program Files (x86)\VSTPlugins

Custom Paths
Network Locations
```

---

## Scan Toolbar

### Scan

Scans all configured folders.

### Rescan All

Forces complete rescan.

### Scan Failed

Displays only failed plugins.

### Stop

Stops current scan.

---

## Scan Options

### Test Editors

Default:

```text
Enabled
```

Validate:

- Plugin loads
- Plugin editor opens
- Plugin editor closes safely

---

### Hide Popups During Scan

Default:

```text
Enabled
```

Suppress:

- Splash screens
- Activation prompts
- Registration windows
- Nag screens

---

## Search

Filters by:

```text
Plugin Name
Vendor
Category
```

---

## Type Filter

Options:

```text
All
Instruments
Effects
Dynamics
EQ
Delay
Reverb
Utility
MIDI
Analyzer
```

---

## Plugin Grid

Columns:

```text
Name
Version
Vendor
Type
Bits
Host
Status
Details
```

---

### Status Values

```text
OK
Warning
Crashed
Missing
Disabled
```

Colour coded:

```text
Green
Amber
Red
Grey
```

---

### Double Click

Action:

```text
Load Plugin
Open Plugin View
```

---

# View 2 - Plugin View

Purpose:

```text
Plugin Operation
Preset Management
Parameter Editing
```

---

## Layout

```text
----------------------------------------------------

Plugin Name

----------------------------------------------------

[← Plugins]

[Patch Combo........................]

[Actions ▼]

[Params]

[Unload]

----------------------------------------------------

           Original Plugin Editor

----------------------------------------------------
```

---

# Plugin Header

Displays:

```text
Plugin Name
Version
Vendor
Bitness
```

Example:

```text
Synth1
Version 1.13
Ichiro Toda
32-bit
```

---

# Patch Combo

Displays:

- Current preset
- Plugin programs
- Renaissance presets

Supports:

```text
Search
Recent Presets
Selection
```

---

# Actions Menu

Items:

```text
Save
Save As
Load
Rename
Delete

Import FXP
Export FXP

Import FXB
Export FXB
```

Future:

```text
Favourite
Tags
Categories
```

---

# Params Dialog

The parameter editor is a key differentiator.

Renaissance must improve significantly on YeOldeVST's generic UI.

---

## Example

```text
------------------------------------------------

Filter

(Dial) Cutoff          12.0kHz

(Dial) Resonance       35%

------------------------------------------------

Amplifier

(Dial) Attack          10ms

(Dial) Decay           100ms

(Dial) Sustain         80%

(Dial) Release         250ms

------------------------------------------------
```

---

# Dial Controls

Inspired by:

```text
FabFilter
Arturia
Bitwig
U-He
```

Features:

- Drag adjust
- Mouse wheel support
- Shift fine adjustment
- Double-click reset
- Tooltips
- Right-click options

---

## Parameter Dialog Features

Supports:

```text
Search Parameters

Modified Only

Automatable Only

Reset Page

Reset All
```

---

# Plugin Editor Area

The original VST2 editor is embedded directly inside Renaissance.

Requirements:

- Focus support
- Resize support
- Mouse support
- Keyboard support
- Recovery after host restart

---

# Unload Button

Performs:

```text
Save State

Close Editor

Disconnect Audio

Release Plugin

Shutdown Host Process
```

Returns to:

```text
Scan View
```

---

# Database Schema

## Plugins

```sql
Plugins
(
    Id,
    Name,
    Vendor,
    Version,
    Type,
    Architecture,
    HostType,
    Path,
    Status
)
```

---

## ScanLocations

```sql
ScanLocations
(
    Id,
    Path
)
```

---

## PluginMetadata

```sql
PluginMetadata
(
    PluginId,
    IconPath,
    Favourite,
    Tags
)
```

---

# MVP Milestones

## Milestone 1

Core Platform

- Solution setup
- Core architecture
- Host32
- Host64
- IPC

---

## Milestone 2

Plugin Hosting

- Load VST2
- Open editor
- Read metadata
- Read parameters

---

## Milestone 3

Audio & MIDI

- Audio processing
- MIDI input
- MIDI output
- Automation

---

## Milestone 4

Preset Engine

- Programs
- FXP
- FXB
- Renaissance presets

---

## Milestone 5

Scanner

- Folder management
- Plugin validation
- Database
- Scan diagnostics

---

## Milestone 6

Modern UI

- Scan View
- Plugin View
- Plugin browser
- Parameter dialog
- Modern dials

---

## Milestone 7

Crash Isolation

- Host recovery
- Crash handling
- Crash logging
- Automatic restart

---

# Non-Negotiable Engineering Rules

### Rule 1

Renaissance VST2 and Renaissance VST3 must be thin wrappers.

---

### Rule 2

Business logic belongs only in:

```text
Renaissance.Core
```

---

### Rule 3

32-bit plugins are never loaded in-process.

---

### Rule 4

All plugin scanning occurs through isolated host processes.

---

### Rule 5

A plugin crash must never crash the DAW.

---

### Rule 6

Audio callbacks must not allocate memory.

---

### Rule 7

Plugin state must be maintained by Renaissance and not solely inside the hosted plugin.

---

### Rule 8

All presets, parameters and state must survive DAW save/load.

---

### Rule 9

Both VST2 and VST3 Renaissance wrappers must provide identical functionality.

---

# MVP Success Criteria

✅ Loads VST2 plugins

✅ Supports 32-bit and 64-bit VST2 plugins

✅ Works inside Reason 10 (VST2)

✅ Works inside Reason 14 (VST3)

✅ Loads original plugin editor

✅ Processes audio

✅ Processes MIDI

✅ Supports automation

✅ Supports FXP/FXB

✅ Supports Renaissance presets

✅ Plugin scanning works

✅ Plugin database works

✅ Crash isolation works

✅ Host restart works

✅ Parameter dialog uses modern rotary dials

✅ Shared functionality in VST2 and VST3 wrappers

✅ Production-ready architecture suitable for future CLAP and plugin-chain expansion