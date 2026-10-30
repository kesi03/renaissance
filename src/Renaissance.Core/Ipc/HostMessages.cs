using Renaissance.Core.Models;

namespace Renaissance.Core.Ipc;

public static class HostCommands
{
    public const string Ping = "Ping";
    public const string Load = "Load";
    public const string Unload = "Unload";
    public const string Shutdown = "Shutdown";
    public const string GetParameters = "GetParameters";
    public const string GetParameterValues = "GetParameterValues";
    public const string GetParameter = "GetParameter";
    public const string SetParameter = "SetParameter";
    public const string GetPrograms = "GetPrograms";
    public const string GetProgram = "GetProgram";
    public const string SetProgram = "SetProgram";
    public const string SetProgramName = "SetProgramName";
    public const string GetChunk = "GetChunk";
    public const string SetChunk = "SetChunk";
    public const string EditorOpen = "EditorOpen";
    public const string EditorAttach = "EditorAttach";
    public const string EditorResize = "EditorResize";
    public const string EditorClose = "EditorClose";
    public const string EditorTest = "EditorTest";
    public const string AudioStart = "AudioStart";
    public const string AudioStop = "AudioStop";
    public const string SendSysEx = "SendSysEx";
    public const string SetHidePopups = "SetHidePopups";
}

public static class HostEvents
{
    public const string ParameterChanged = "ParameterChanged";
    public const string EditorResizeRequested = "EditorResizeRequested";
    public const string IoChanged = "IoChanged";
}

/// <summary>Request sent from Renaissance.Core to a Host32/Host64 process.</summary>
public sealed class HostRequest
{
    public int Id { get; set; }
    public string Command { get; set; } = "";
    public string? Path { get; set; }
    public string? Text { get; set; }
    public int Index { get; set; }
    public float Value { get; set; }
    public bool Flag { get; set; }
    public byte[]? Data { get; set; }
    public long Handle { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double SampleRate { get; set; }
    public int BlockSize { get; set; }
    public int Inputs { get; set; }
    public int Outputs { get; set; }
}

/// <summary>Response (Id &gt; 0) or unsolicited event (Id == 0, Event set) from a host process.</summary>
public sealed class HostResponse
{
    public int Id { get; set; }
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public string? Event { get; set; }
    public int Index { get; set; }
    public float Value { get; set; }
    public string? Text { get; set; }
    public byte[]? Data { get; set; }
    public long Handle { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public PluginMetadata? Metadata { get; set; }
    public List<ParameterInfo>? Parameters { get; set; }
    public List<string>? Programs { get; set; }
    public float[]? Floats { get; set; }
}
