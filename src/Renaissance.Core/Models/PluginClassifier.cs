namespace Renaissance.Core.Models;

public static class PluginClassifier
{
    public static string Classify(PluginMetadata m)
    {
        if (m.IsSynth || m.Category is 2 or 11) return PluginCategories.Instruments;
        if (m.NumOutputs == 0 && m.ReceivesMidi) return PluginCategories.Midi;
        if (m.Category == 3) return PluginCategories.Analyzer;
        if (m.Category == 4) return PluginCategories.Dynamics;
        if (m.Category == 6) return PluginCategories.Reverb;
        if (m.Category == 8) return PluginCategories.Utility;

        var n = (string.IsNullOrWhiteSpace(m.Name) ? m.Product : m.Name).ToLowerInvariant();
        if (HasAny(n, "reverb", "room", "hall", "plate", "verb")) return PluginCategories.Reverb;
        if (HasAny(n, "delay", "echo")) return PluginCategories.Delay;
        if (HasAny(n, "comp", "limit", "gate", "expand", "dynamic", "maxim", "clip")) return PluginCategories.Dynamics;
        if (HasAny(n, " eq", "eq ", "equal", "parametric")) return PluginCategories.EQ;
        if (HasAny(n, "analy", "meter", "scope", "spectrum", "tuner")) return PluginCategories.Analyzer;
        if (HasAny(n, "midi", "arp")) return PluginCategories.Midi;
        if (HasAny(n, "util", "gain", "pan", "mono", "stereo", "width")) return PluginCategories.Utility;
        return PluginCategories.Effects;
    }

    private static bool HasAny(string s, params string[] needles) => needles.Any(s.Contains);
}

/// <summary>Heuristic grouping of generic parameters into "pages" for the parameter editor.</summary>
public static class ParameterGrouper
{
    private static readonly (string Group, string[] Keys)[] Rules =
    [
        ("Filter", ["filter", "cutoff", "reso", "flt", "freq", "q ", "drive", "formant"]),
        ("Amplifier", ["attack", "decay", "sustain", "release", "amp", "env", "adsr", "gate"]),
        ("Oscillators", ["osc", "wave", "tune", "pitch", "detune", "unison", "semi", "sub", "noise", "pulse", "pwm"]),
        ("Modulation", ["lfo", "mod", "rate", "depth", "speed", "vibrato", "tremolo"]),
        ("Effects", ["reverb", "delay", "chorus", "flang", "phas", "dist", "fx", "eff", "echo", "wet", "dry", "mix"]),
        ("Master", ["master", "volume", "level", "gain", "output", "pan", "main", "vol"]),
    ];

    public const string Other = "Other";

    public static string GroupFor(string parameterName)
    {
        var n = parameterName.ToLowerInvariant();
        foreach (var (group, keys) in Rules)
            if (keys.Any(n.Contains)) return group;
        return Other;
    }

    public static IReadOnlyList<string> Order { get; } =
        [.. Rules.Select(r => r.Group), Other];
}
