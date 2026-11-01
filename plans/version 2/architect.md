# Renaissance Audio & MIDI Platform
## Architecture & Code Examples

---

# High Level Architecture

```text
┌──────────────────────────────┐
│ Renaissance UI               │
└──────────────┬───────────────┘
               │
┌──────────────▼───────────────┐
│ Renaissance.Media.Core       │
└──────┬────────┬────────┬─────┘
       │        │        │

       ▼        ▼        ▼

   Audio     MIDI     Clock

       ▼        ▼        ▼

     Routing Engine

              ▼

        Recording
```

---

# Solution Structure

```text
Renaissance.Media.sln

src/

Renaissance.Media.Abstractions
Renaissance.Media.Devices
Renaissance.Media.Audio
Renaissance.Media.Midi
Renaissance.Media.Clock
Renaissance.Media.Mixer
Renaissance.Media.Routing
Renaissance.Media.Recording
Renaissance.Media.UI

tests/

Renaissance.Media.Tests
```

---

# Audio Device

```csharp
public interface IAudioDevice
{
    string Id { get; }

    string Name { get; }

    int InputChannels { get; }

    int OutputChannels { get; }
}
```

---

# MIDI Device

```csharp
public interface IMidiDevice
{
    string Id { get; }

    string Name { get; }

    bool SupportsMidi2 { get; }
}
```

---

# Audio Engine

```csharp
public interface IAudioEngine
{
    Task StartAsync();

    Task StopAsync();

    int SampleRate { get; }

    int BufferSize { get; }
}
```

---

# Midi Engine

```csharp
public interface IMidiEngine
{
    Task OpenInputAsync(string id);

    Task OpenOutputAsync(string id);

    Task SendAsync(IMidiMessage message);
}
```

---

# Mixer Channel

```csharp
public class MixerChannel
{
    public string Name { get; set; }

    public float Volume { get; set; }

    public float Pan { get; set; }

    public bool Mute { get; set; }

    public bool Solo { get; set; }
}
```

---

# Audio Graph

```text
Audio Input
      │
      ▼

Gain Node

      ▼

Pan Node

      ▼

Mixer Channel

      ▼

Master Bus

      ▼

Audio Output
```

---

# MIDI Graph

```text
Midi Input

    ▼

Midi Filter

    ▼

Midi Router

    ▼

Plugin

    ▼

Midi Output
```

---

# Recording Pipeline

```text
Audio Engine
      │
      ▼

Recording Bus

      ▼

Encoder

      ▼

WAV / MP3 / AIFF
```

---

# Transport Architecture

```text
Internal Clock
       │

MIDI Clock
       │

MTC
       │

Ableton Link
       │

Transport Service
       │

Consumers
```

---

# Plugin Modes

```text
Standalone
    │
    ├── Audio Devices
    ├── MIDI Devices
    └── Mixer

CLAP
    │
    ├── Host Audio
    ├── Host MIDI
    └── Host Transport

VST2
    │
    ├── Host Audio
    ├── Host MIDI
    └── Host Transport

VST3
    │
    ├── Host Audio
    ├── Host MIDI
    └── Host Transport
```

---

# Recommended Libraries

```text
NAudio
CSCore
Microsoft MIDI Services
Microsoft.Windows.Devices.Midi2
MeltySynth
ManagedBass (optional)
NAudio.Asio
```

---

# Dependency Direction

```text
UI
 ↓

Mixer
 ↓

Routing
 ↓

Audio / MIDI / Clock
 ↓

Abstractions

No upward dependencies allowed.
```