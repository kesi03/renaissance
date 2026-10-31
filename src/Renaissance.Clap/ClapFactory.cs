using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Renaissance.Clap;

/// <summary>
/// The CLAP plugin factory. Like the VST3 factory it exposes two classes from one binary:
/// the instrument ("Renaissance") and the audio effect ("Renaissance FX"). Everything the host
/// receives is allocated in unmanaged memory and lives until <c>clap_entry.deinit()</c>.
/// </summary>
internal static unsafe class ClapFactory
{
    private static readonly object Gate = new();
    private static bool _initialized;

    private static clap_plugin_factory* _factory;
    private static clap_plugin_descriptor* _descriptors;

    public static void* Get()
    {
        EnsureInitialized();
        return _factory;
    }

    public static clap_plugin_descriptor* InstrumentDescriptor
    {
        get
        {
            EnsureInitialized();
            return &_descriptors[0];
        }
    }

    public static clap_plugin_descriptor* EffectDescriptor
    {
        get
        {
            EnsureInitialized();
            return &_descriptors[1];
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;
            Build();
            _initialized = true;
        }
    }

    private static void Build()
    {
        var version = Strings.Utf8(Renaissance.Core.AppVersion.FourPart);

        _descriptors = (clap_plugin_descriptor*)NativeMemory.AllocZeroed(
            (nuint)(2 * sizeof(clap_plugin_descriptor)));

        _descriptors[0] = Descriptor(
            id: "com.mockholm.renaissance",
            name: "Renaissance",
            version: version,
            description: "Renaissance instrument",
            features: Features("instrument", "synthesizer", "stereo"));

        _descriptors[1] = Descriptor(
            id: "com.mockholm.renaissance-fx",
            name: "Renaissance FX",
            version: version,
            description: "Renaissance audio effect",
            features: Features("audio-effect", "stereo"));

        _factory = (clap_plugin_factory*)NativeMemory.AllocZeroed((nuint)sizeof(clap_plugin_factory));
        _factory->get_plugin_count = &GetPluginCount;
        _factory->get_plugin_descriptor = &GetPluginDescriptor;
        _factory->create_plugin = &CreatePlugin;
    }

    private static clap_plugin_descriptor Descriptor(
        string id,
        string name,
        byte* version,
        string description,
        byte** features) => new()
    {
        clap_version = ClapVersion.Current,
        id = Strings.Utf8(id),
        name = Strings.Utf8(name),
        vendor = Strings.Utf8("Renaissance"),
        url = Strings.Utf8("https://github.com/mockholm"),
        manual_url = null,
        support_url = null,
        version = version,
        description = Strings.Utf8(description),
        features = features
    };

    private static byte** Features(params string[] names)
    {
        var array = (byte**)NativeMemory.AllocZeroed((nuint)((names.Length + 1) * sizeof(byte*)));
        for (var i = 0; i < names.Length; i++) array[i] = Strings.Utf8(names[i]);
        return array;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint GetPluginCount(clap_plugin_factory* factory) => 2;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static clap_plugin_descriptor* GetPluginDescriptor(clap_plugin_factory* factory, uint index)
    {
        EnsureInitialized();
        return index < 2 ? &_descriptors[index] : null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static clap_plugin* CreatePlugin(clap_plugin_factory* factory, clap_host* host, byte* pluginId)
    {
        try
        {
            var id = pluginId is null ? null : Marshal.PtrToStringUTF8((IntPtr)pluginId);
            Log.Write($"create_plugin: {id}");

            if (id == "com.mockholm.renaissance")
                return ClapPluginInstance.Create(host, instrument: true, InstrumentDescriptor);

            if (id == "com.mockholm.renaissance-fx")
                return ClapPluginInstance.Create(host, instrument: false, EffectDescriptor);

            return null;
        }
        catch (Exception ex)
        {
            Log.Write("create_plugin failed: " + ex);
            return null;
        }
    }
}
