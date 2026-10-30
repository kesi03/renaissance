namespace Renaissance.Core;

/// <summary>
/// The product version, taken from version.txt at build time (see Directory.Build.props). Shared by the app,
/// the VST2/VST3 wrappers and the installer so they always agree.
/// </summary>
public static class AppVersion
{
    public static Version Current { get; } = Read();

    /// <summary>MAJOR.MINOR.PATCH, e.g. "0.1.0".</summary>
    public static string Text => $"{Current.Major}.{Current.Minor}.{Math.Max(0, Current.Build)}";

    /// <summary>Four-part form used by VST3 class info, e.g. "0.1.0.0".</summary>
    public static string FourPart => Text + ".0";

    /// <summary>VST2 vendor version: 0.1.0 becomes 100, 1.2.3 becomes 1230 (shown by hosts in the same decimal style as most plugins).</summary>
    public static int Vst2Number => Current.Major * 1000 + Current.Minor * 100 + Math.Max(0, Current.Build) * 10;

    private static Version Read()
    {
        try { return typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0, 0); }
        catch { return new Version(0, 0, 0, 0); }
    }
}
