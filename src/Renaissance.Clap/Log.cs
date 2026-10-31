namespace Renaissance.Clap;

internal static class Log
{
    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Renaissance",
                "logs");

            Directory.CreateDirectory(dir);

            File.AppendAllText(
                Path.Combine(dir, "clap.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}