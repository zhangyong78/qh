namespace Qh.Core;

public static class PythonRuntime
{
    public static string ResolveExecutable()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "python", OperatingSystem.IsWindows() ? "python.exe" : "python");
        if (File.Exists(bundled)) return bundled;
        var configured = Environment.GetEnvironmentVariable("QH_PYTHON_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        return "python";
    }
}
