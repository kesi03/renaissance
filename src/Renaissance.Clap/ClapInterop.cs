using System.Runtime.InteropServices;

namespace Renaissance.Clap;

/// <summary>
/// CLAP 1.2 ABI constants. Kept in one place so the managed callbacks and the native
/// <c>clap_entry</c> shim (clap_entry.c) agree on the version and extension ids.
/// </summary>
internal static class ClapConstants
{
    public const string PluginFactoryId = "clap.plugin-factory";

    public const string ExtAudioPorts = "clap.audio-ports";
    public const string ExtNotePorts = "clap.note-ports";
    public const string ExtParams = "clap.params";
    public const string ExtState = "clap.state";
    public const string ExtGui = "clap.gui";
    public const string ExtLatency = "clap.latency";

    public const string PortMono = "mono";
    public const string PortStereo = "stereo";

    public const string WindowApiWin32 = "win32";

    public const uint VersionMajor = 1;
    public const uint VersionMinor = 2;
    public const uint VersionRevision = 10;

    public const uint InvalidId = uint.MaxValue;

    public const ushort CoreEventSpaceId = 0;

    public const ushort EventNoteOn = 0;
    public const ushort EventNoteOff = 1;
    public const ushort EventNoteChoke = 2;
    public const ushort EventNoteEnd = 3;
    public const ushort EventNoteExpression = 4;
    public const ushort EventParamValue = 5;
    public const ushort EventParamMod = 6;
    public const ushort EventParamGestureBegin = 7;
    public const ushort EventParamGestureEnd = 8;
    public const ushort EventTransport = 9;
    public const ushort EventMidi = 10;
    public const ushort EventMidiSysex = 11;
    public const ushort EventMidi2 = 12;

    public const int ProcessError = 0;
    public const int ProcessContinue = 1;
    public const int ProcessContinueIfNotQuiet = 2;
    public const int ProcessTail = 3;
    public const int ProcessSleep = 4;

    public const uint AudioPortIsMain = 1 << 0;

    public const uint NoteDialectClap = 1 << 0;
    public const uint NoteDialectMidi = 1 << 1;
    public const uint NoteDialectMidiMpe = 1 << 2;
    public const uint NoteDialectMidi2 = 1 << 3;

    public const uint ParamIsHidden = 1 << 2;
    public const uint ParamIsReadonly = 1 << 3;
    public const uint ParamIsAutomatable = 1 << 5;
}

#region Version

[StructLayout(LayoutKind.Sequential)]
internal struct clap_version
{
    public uint major;
    public uint minor;
    public uint revision;
}

internal static class ClapVersion
{
    public static readonly clap_version Current = new()
    {
        major = ClapConstants.VersionMajor,
        minor = ClapConstants.VersionMinor,
        revision = ClapConstants.VersionRevision
    };
}

#endregion

#region Plugin Factory / Descriptor / Plugin

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_plugin_factory
{
    public delegate* unmanaged[Cdecl]<clap_plugin_factory*, uint> get_plugin_count;

    public delegate* unmanaged[Cdecl]<clap_plugin_factory*, uint, clap_plugin_descriptor*> get_plugin_descriptor;

    public delegate* unmanaged[Cdecl]<clap_plugin_factory*, clap_host*, byte*, clap_plugin*> create_plugin;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_plugin_descriptor
{
    public clap_version clap_version;

    public byte* id;
    public byte* name;
    public byte* vendor;
    public byte* url;
    public byte* manual_url;
    public byte* support_url;
    public byte* version;
    public byte* description;

    // Null-terminated array of UTF-8 strings.
    public byte** features;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_plugin
{
    public clap_plugin_descriptor* desc;

    public void* plugin_data;

    public delegate* unmanaged[Cdecl]<clap_plugin*, byte> init;
    public delegate* unmanaged[Cdecl]<clap_plugin*, void> destroy;

    public delegate* unmanaged[Cdecl]<clap_plugin*, double, uint, uint, byte> activate;
    public delegate* unmanaged[Cdecl]<clap_plugin*, void> deactivate;

    public delegate* unmanaged[Cdecl]<clap_plugin*, byte> start_processing;
    public delegate* unmanaged[Cdecl]<clap_plugin*, void> stop_processing;
    public delegate* unmanaged[Cdecl]<clap_plugin*, void> reset;

    public delegate* unmanaged[Cdecl]<clap_plugin*, clap_process*, int> process;

    public delegate* unmanaged[Cdecl]<clap_plugin*, byte*, void*> get_extension;

    public delegate* unmanaged[Cdecl]<clap_plugin*, void> on_main_thread;
}

#endregion

#region Host

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_host
{
    public clap_version clap_version;

    public void* host_data;

    public byte* name;
    public byte* vendor;
    public byte* url;
    public byte* version;

    public delegate* unmanaged[Cdecl]<clap_host*, byte*, void*> get_extension;

    public delegate* unmanaged[Cdecl]<clap_host*, void> request_restart;
    public delegate* unmanaged[Cdecl]<clap_host*, void> request_process;
    public delegate* unmanaged[Cdecl]<clap_host*, void> request_callback;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_host_gui
{
    public delegate* unmanaged[Cdecl]<clap_host*, void> resize_hints_changed;
    public delegate* unmanaged[Cdecl]<clap_host*, uint, uint, byte> request_resize;
    public delegate* unmanaged[Cdecl]<clap_host*, byte> request_show;
    public delegate* unmanaged[Cdecl]<clap_host*, byte> request_hide;
    public delegate* unmanaged[Cdecl]<clap_host*, byte, void> closed;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_host_state
{
    public delegate* unmanaged[Cdecl]<clap_host*, void> mark_dirty;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_host_params
{
    public delegate* unmanaged[Cdecl]<clap_host*, uint, void> rescan;
    public delegate* unmanaged[Cdecl]<clap_host*, uint, uint, void> clear;
    public delegate* unmanaged[Cdecl]<clap_host*, void> request_flush;
}

#endregion

#region Process / Audio Buffer / Events

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_process
{
    public long steady_time;
    public uint frames_count;

    public clap_event_transport* transport;

    public clap_audio_buffer* audio_inputs;
    public clap_audio_buffer* audio_outputs;
    public uint audio_inputs_count;
    public uint audio_outputs_count;

    public clap_input_events* in_events;
    public clap_output_events* out_events;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_audio_buffer
{
    public float** data32;
    public double** data64;
    public uint channel_count;
    public uint latency;
    public ulong constant_mask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct clap_event_header
{
    public uint size;
    public uint time;
    public ushort space_id;
    public ushort type;
    public uint flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct clap_event_note
{
    public clap_event_header header;

    public int note_id;
    public short port_index;
    public short channel;
    public short key;
    public double velocity;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_event_param_value
{
    public clap_event_header header;

    public uint param_id;
    public void* cookie;

    public int note_id;
    public short port_index;
    public short channel;
    public short key;

    public double value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_event_param_mod
{
    public clap_event_header header;

    public uint param_id;
    public void* cookie;

    public int note_id;
    public short port_index;
    public short channel;
    public short key;

    public double amount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct clap_event_param_gesture
{
    public clap_event_header header;
    public uint param_id;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_event_midi
{
    public clap_event_header header;

    public ushort port_index;
    public fixed byte data[3];
}

[StructLayout(LayoutKind.Sequential)]
internal struct clap_event_transport
{
    public clap_event_header header;
    public uint flags;
    public long song_pos_beats;
    public long song_pos_seconds;
    public double tempo;
    public double tempo_inc;
    public long loop_start_beats;
    public long loop_end_beats;
    public long loop_start_seconds;
    public long loop_end_seconds;
    public long bar_start;
    public int bar_number;
    public ushort tsig_num;
    public ushort tsig_denom;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_input_events
{
    public void* ctx;

    public delegate* unmanaged[Cdecl]<clap_input_events*, uint> size;
    public delegate* unmanaged[Cdecl]<clap_input_events*, uint, clap_event_header*> get;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_output_events
{
    public void* ctx;

    public delegate* unmanaged[Cdecl]<clap_output_events*, clap_event_header*, byte> try_push;
}

#endregion

#region Extensions

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_audio_port_info
{
    public uint id;
    public fixed byte name[256];
    public uint flags;
    public uint channel_count;
    public byte* port_type;
    public uint in_place_pair;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_plugin_audio_ports
{
    public delegate* unmanaged[Cdecl]<clap_plugin*, byte, uint> count;
    public delegate* unmanaged[Cdecl]<clap_plugin*, uint, byte, clap_audio_port_info*, byte> get;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_note_port_info
{
    public uint id;
    public uint supported_dialects;
    public uint preferred_dialect;
    public fixed byte name[256];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_plugin_note_ports
{
    public delegate* unmanaged[Cdecl]<clap_plugin*, byte, uint> count;
    public delegate* unmanaged[Cdecl]<clap_plugin*, uint, byte, clap_note_port_info*, byte> get;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_param_info
{
    public uint id;
    public uint flags;
    public void* cookie;
    public fixed byte name[256];
    public fixed byte module[1024];
    public double min_value;
    public double max_value;
    public double default_value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_plugin_params
{
    public delegate* unmanaged[Cdecl]<clap_plugin*, uint> count;
    public delegate* unmanaged[Cdecl]<clap_plugin*, uint, clap_param_info*, byte> get_info;
    public delegate* unmanaged[Cdecl]<clap_plugin*, uint, double*, byte> get_value;
    public delegate* unmanaged[Cdecl]<clap_plugin*, uint, double, byte*, uint, byte> value_to_text;
    public delegate* unmanaged[Cdecl]<clap_plugin*, uint, byte*, double*, byte> text_to_value;
    public delegate* unmanaged[Cdecl]<clap_plugin*, clap_input_events*, clap_output_events*, void> flush;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_istream
{
    public void* ctx;
    public delegate* unmanaged[Cdecl]<clap_istream*, void*, ulong, long> read;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_ostream
{
    public void* ctx;
    public delegate* unmanaged[Cdecl]<clap_ostream*, void*, ulong, long> write;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_plugin_state
{
    public delegate* unmanaged[Cdecl]<clap_plugin*, clap_ostream*, byte> save;
    public delegate* unmanaged[Cdecl]<clap_plugin*, clap_istream*, byte> load;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_window
{
    public byte* api;
    public void* ptr;
}

[StructLayout(LayoutKind.Sequential)]
internal struct clap_gui_resize_hints
{
    public byte can_resize_horizontally;
    public byte can_resize_vertically;
    public byte preserve_aspect_ratio;
    public uint aspect_ratio_width;
    public uint aspect_ratio_height;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_plugin_gui
{
    public delegate* unmanaged[Cdecl]<clap_plugin*, byte*, byte, byte> is_api_supported;
    public delegate* unmanaged[Cdecl]<clap_plugin*, byte**, byte*, byte> get_preferred_api;
    public delegate* unmanaged[Cdecl]<clap_plugin*, byte*, byte, byte> create;
    public delegate* unmanaged[Cdecl]<clap_plugin*, void> destroy;
    public delegate* unmanaged[Cdecl]<clap_plugin*, double, byte> set_scale;
    public delegate* unmanaged[Cdecl]<clap_plugin*, uint*, uint*, byte> get_size;
    public delegate* unmanaged[Cdecl]<clap_plugin*, byte> can_resize;
    public delegate* unmanaged[Cdecl]<clap_plugin*, clap_gui_resize_hints*, byte> get_resize_hints;
    public delegate* unmanaged[Cdecl]<clap_plugin*, uint*, uint*, byte> adjust_size;
    public delegate* unmanaged[Cdecl]<clap_plugin*, uint, uint, byte> set_size;
    public delegate* unmanaged[Cdecl]<clap_plugin*, clap_window*, byte> set_parent;
    public delegate* unmanaged[Cdecl]<clap_plugin*, clap_window*, byte> set_transient;
    public delegate* unmanaged[Cdecl]<clap_plugin*, byte*, void> suggest_title;
    public delegate* unmanaged[Cdecl]<clap_plugin*, byte> show;
    public delegate* unmanaged[Cdecl]<clap_plugin*, byte> hide;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct clap_plugin_latency
{
    public delegate* unmanaged[Cdecl]<clap_plugin*, uint> get;
}

#endregion
