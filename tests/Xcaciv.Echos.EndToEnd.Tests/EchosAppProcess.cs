using System.IO;
using System.Collections;
using System.Diagnostics;

namespace Xcaciv.Echos.EndToEnd.Tests;

internal sealed class EchosAppProcess : IDisposable
{
    private const string WindowTitle = "Echos";
    private readonly Process _process;

    private EchosAppProcess(Process process) => _process = process;

    public IntPtr MainWindowHandle
    {
        get
        {
            _process.Refresh();
            return _process.MainWindowHandle;
        }
    }

    public bool HasExited => _process.HasExited;

    public static EchosAppProcess Start(IReadOnlyDictionary<string, string> environment)
    {
        var pathFile = Path.Combine(AppContext.BaseDirectory, "EchosApp.path");
        if (!File.Exists(pathFile))
            throw new InvalidOperationException("EchosApp.path was not found beside the test assembly. Build the test project without BuildEchosApp=false.");
        var exe = File.ReadAllLines(pathFile).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            throw new InvalidOperationException("The Echos app executable recorded in EchosApp.path does not exist.");

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = (string)entry.Key;
            if (!name.StartsWith("ECHOS_", StringComparison.OrdinalIgnoreCase))
                variables[name] = entry.Value as string ?? string.Empty;
        }
        foreach (var pair in environment)
            variables[pair.Key] = pair.Value;
        if (!variables.ContainsKey("DOTNET_ROLL_FORWARD"))
            variables["DOTNET_ROLL_FORWARD"] = "Major";
        info.Environment.Clear();
        foreach (var pair in variables)
            info.Environment[pair.Key] = pair.Value;

        var process = Process.Start(info) ?? throw new InvalidOperationException("The Echos app process did not start.");
        return new EchosAppProcess(process);
    }

    public async Task WaitForMainWindowAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (_process.HasExited)
                throw new InvalidOperationException("The Echos app exited before its window appeared (exit code 0x" + _process.ExitCode.ToString("X") + ").");
            _process.Refresh();
            if (_process.MainWindowHandle != IntPtr.Zero && _process.MainWindowTitle == WindowTitle)
                return;
            await Task.Delay(100);
        }
        Kill();
        throw new TimeoutException("The Echos main window did not appear in time.");
    }

    public async Task<int> CloseAndWaitForExitAsync(TimeSpan timeout)
    {
        if (!_process.CloseMainWindow())
            throw new InvalidOperationException("CloseMainWindow could not send the close request.");
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Kill();
            throw new TimeoutException("The Echos app did not exit after the close request.");
        }
        return _process.ExitCode;
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
        {
        }
    }
}
