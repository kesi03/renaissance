# Renaissance Audio & MIDI Platform
## Functional Specification

Version: 1.0

---

# 1. Overview

The Renaissance platform shall operate in the following modes:

- Standalone Application
- Plugins are implemented already
    - CLAP Plugin
    - VST2 Plugin
    - VST3 Plugin

The same audio and MIDI infrastructure shall be shared across all modes.

---

# 2. Goals

The platform must provide:

- Audio Input
- Audio Output
- MIDI Input
- MIDI Output
- Virtual MIDI Ports
- Audio Routing
- MIDI Routing
- Recording
- Clock Synchronisation
- Mixer
- Device Aggregation

---

# 3. Operating Modes

## 3.1 Standalone Mode

The application shall:

- Connect directly to audio hardware
- Connect directly to MIDI hardware
- Create virtual MIDI devices
- Connect to existing virtual MIDI devices
- Record audio
- Record MIDI
- Synchronise to external clocks

---

## 3.2 Plugin Mode

Supported plugin formats:

- CLAP
- VST2
- VST3

The plugin shall:

- Receive audio from host
- Return audio to host
- Receive MIDI from host
- Return MIDI to host
- Receive host transport information
- Receive host tempo information

The plugin shall not directly open audio devices.

The plugin shall not directly open hardware MIDI devices unless explicitly enabled by the user.

---

# 4. Audio Requirements

Supported APIs:

- WASAPI Shared
- WASAPI Exclusive
- ASIO
- WaveOut
- DirectSound

Supported sample rates:

- 44100 Hz
- 48000 Hz
- 88200 Hz
- 96000 Hz
- 192000 Hz

Supported buffer sizes:

- 64
- 128
- 256
- 512
- 1024
- 2048

Audio channel types:

- Mono
- Stereo
- Multi-channel

---

# 5. MIDI Requirements

Supported standards:

- MIDI 1.0
- MIDI 2.0
- Universal MIDI Packet (UMP)

Supported messages:

- Note On
- Note Off
- Aftertouch
- Poly Aftertouch
- CC
- Program Change
- Pitch Bend
- SysEx
- MIDI 2.0 Messages

---

# 6. Virtual MIDI Requirements

Users shall be able to:

- Create virtual MIDI ports
- Delete virtual MIDI ports
- Rename virtual MIDI ports
- Connect to virtual MIDI ports
- Use virtual MIDI ports between applications

Examples:

- Renaissance → Reaper
- Renaissance → Cubase
- Renaissance → Standalone Synth
- External Application → Renaissance

---

# 7. Mixer Requirements

The mixer shall support:

- Input Channels
- Output Channels
- Auxiliary Sends
- Group Buses
- Master Bus

Per-channel controls:

- Gain
- Volume
- Pan
- Mute
- Solo
- Peak Meter
- RMS Meter

---

# 8. Device Aggregation

Users shall be able to combine:

- Multiple audio inputs
- Multiple audio outputs

Into a single logical device.

Example:

Input Device:
Focusrite Scarlett

Output Device:
Behringer UMC

Presented as:

Aggregate Device

The system shall provide:

- Latency Compensation
- Clock Drift Monitoring
- Sample Alignment

---

# 9. Transport and Clocks

Supported clock sources:

- Internal Clock
- MIDI Clock
- MIDI Time Code
- Ableton Link

Modes:

- Master
- Slave

---

# 10. Recording Requirements

Audio formats:

- WAV
- AIFF
- MP3
- FLAC

MIDI formats:

- MID
- MIDI 2.0
- UMP

Combined recording:

- Audio
- MIDI
- Tempo
- Markers

---

# 11. Project Recording

The application shall support saving sessions as:

.rensession

The session shall contain:

- Routing
- Mixer State
- MIDI Configuration
- Device Configuration
- Transport Information

---

# 12. Future Extensions

Potential future support:

- JACK
- RTP MIDI
- VBAN
- NDI Audio
- REX Import
- Network Audio Devices