using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Xcaciv.Echos.EndToEnd.Tests;

internal sealed class ForegroundTestWindow : IDisposable
{
    private readonly Point _location;
    private volatile Form? _form;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _shown = new();
    private Exception? _startupError;

    public ForegroundTestWindow(string? labelText = null, Point? location = null)
    {
        var id = Guid.NewGuid().ToString("N").Substring(0, 8);
        Title = "Echos E2E " + id;
        LabelText = labelText ?? "ECHOS E2E LABEL " + id;
        ValueText = "ECHOS E2E VALUE " + id;
        SecretText = "SECRET " + id;
        _location = location ?? new Point(40, 40);
        _thread = new Thread(Run) { IsBackground = true, Name = "Echos E2E window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_shown.Wait(TimeSpan.FromSeconds(15)))
        {
            Dispose();
            throw new InvalidOperationException("The test window did not open.", _startupError);
        }
    }

    public string Title { get; }
    public string LabelText { get; }
    public string ValueText { get; }
    public string SecretText { get; }
    public IntPtr Handle { get; private set; }
    public Point SolidColorSampleOnScreen { get; private set; }
    public Color SolidColor { get; } = Color.FromArgb(0, 180, 255);

    public void BringToForeground()
    {
        var form = _form ?? throw new InvalidOperationException("The test window is not open.");
        form.Invoke(form.Activate);
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(Handle);
            BringWindowToTop(Handle);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
        var clock = Stopwatch.StartNew();
        while (GetForegroundWindow() != Handle)
        {
            if (clock.Elapsed >= TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("The test window could not be brought to the foreground.");
            Thread.Sleep(50);
        }
    }

    public void Dispose()
    {
        try
        {
            var form = _form;
            if (form is { IsHandleCreated: true })
                form.BeginInvoke(form.Close);
        }
        catch (InvalidOperationException)
        {
            // The form thread has already ended.
        }
        _thread.Join(TimeSpan.FromSeconds(5));
        _shown.Dispose();
    }

    // The form is created on its own thread: constructing a control installs a WinForms SynchronizationContext on the
    // creating thread, which would stall every later await in a test that created the window.
    private Form BuildForm()
    {
        var form = new Form
        {
            Text = Title,
            TopMost = true,
            StartPosition = FormStartPosition.Manual,
            Location = _location,
            ClientSize = new Size(720, 360),
            BackColor = SolidColor,
            ShowInTaskbar = false
        };
        form.Controls.Add(new Label
        {
            Text = LabelText,
            Font = new Font("Arial", 36, FontStyle.Bold),
            ForeColor = Color.Black,
            BackColor = Color.White,
            AutoSize = true,
            Location = new Point(20, 20)
        });
        form.Controls.Add(new TextBox { Text = ValueText, Location = new Point(20, 120), Width = 400 });
        form.Controls.Add(new TextBox
        {
            UseSystemPasswordChar = true,
            Text = SecretText,
            Location = new Point(20, 160),
            Width = 400
        });
        form.Shown += (_, _) =>
        {
            SolidColorSampleOnScreen = form.PointToScreen(new Point(360, 320));
            Handle = form.Handle;
            try
            {
                _shown.Set();
            }
            catch (ObjectDisposedException)
            {
                // The constructor timed out and disposed the window before it was shown.
            }
        };
        return form;
    }

    private void Run()
    {
        try
        {
            var form = BuildForm();
            _form = form;
            Application.Run(form);
        }
        catch (Exception error)
        {
            _startupError = error;
        }
        finally
        {
            _form?.Dispose();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
