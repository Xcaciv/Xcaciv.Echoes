using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.App;

public sealed partial class MainWindow : Window
{
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(3);
    private readonly CaptureConsent consent;
    private readonly AppConfiguration configuration;
    private readonly Func<IContextEngine> createEngine;
    private readonly Func<IAudioCapture> createAudio;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly WindowAffordances affordances;
    private readonly ILogger<MainWindow> logger;
    private readonly object cycleGate = new();
    private CancellationTokenSource cycleCancellation = new();
    private Task activeCycle = Task.CompletedTask;
    private Task audioLifecycle = Task.CompletedTask;
    private Task lateShutdownWork = Task.CompletedTask;
    private Task? stopTask;
    private IContextEngine? engine;
    private IAudioCapture? audio;
    private bool ready;
    private bool hidden;
    private bool applyingConsent;
    private long consentVersion;

    public bool IsStopping { get; private set; }
    public event EventHandler? ExitRequested;
    internal Task LateShutdownWork => lateShutdownWork;

    public MainWindow(CaptureConsent consent, AppConfiguration configuration, Func<IContextEngine> createEngine,
        Func<IAudioCapture> createAudio, ILogger<MainWindow> logger)
    {
        this.consent = consent;
        this.configuration = configuration;
        this.createEngine = createEngine;
        this.createAudio = createAudio;
        this.logger = logger;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        var scale = WindowAffordances.GetScale(this);
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min((int)(440 * scale), workArea.Width),
            Math.Min((int)(760 * scale), workArea.Height)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
        }
        affordances = new WindowAffordances(this, ToggleVisibility, RequestExit);
        ShortcutText.Text = affordances.HasHotKey
            ? "Ctrl+Shift+E shows / hides Echos."
            : "Ctrl+Shift+E is unavailable (possibly already registered).";
        ShortcutText.Text += affordances.HasTray
            ? " Tray icon: click to show / hide; right-click to exit."
            : " Tray unavailable.";
        HideButton.IsEnabled = affordances.HasHotKey || affordances.HasTray;
        DisclosureText.Text = configuration.Disclosure;
        AudioToggle.IsEnabled = configuration.HasTranscriptionConfiguration;
        timer.Tick += OnTick;
        consent.Changed += OnCaptureConsentChanged;
        ready = true;
        if (!configuration.HasLlmConfiguration)
        {
            ObservationToggle.IsEnabled = false;
            StatusText.Text = "Set ECHOS_LLM_ENDPOINT and ECHOS_LLM_MODEL, then restart. Endpoints must use HTTPS (or loopback HTTP). API keys are optional environment settings.";
        }
        else if (!configuration.HasTranscriptionConfiguration)
        {
            StatusText.Text = "Observation is off. Audio requires ECHOS_TRANSCRIPTION_ENDPOINT and ECHOS_TRANSCRIPTION_MODEL, then restart.";
        }
    }

    private void OnConsentChanged(object sender, RoutedEventArgs args) => ApplyConsentState(fromToggles: true);

    private void OnCaptureConsentChanged(object? sender, EventArgs args)
    {
        // Our own toggle application raises Changed synchronously on the UI thread; ApplyConsentState handles it.
        if (DispatcherQueue.HasThreadAccess)
        {
            if (!applyingConsent)
                ApplyConsentState(fromToggles: false);
        }
        else
        {
            InvalidateCycle();
            DispatcherQueue.TryEnqueue(() => ApplyConsentState(fromToggles: false, invalidateCycle: false));
        }
    }

    // UI thread only. Owns the timer and audio scheduling; cycle invalidation is thread-safe.
    private void ApplyConsentState(bool fromToggles, bool invalidateCycle = true)
    {
        if (IsStopping || (fromToggles && !ready))
            return;
        if (invalidateCycle)
            InvalidateCycle();
        SuggestionText.Text = string.Empty;

        if (fromToggles)
        {
            applyingConsent = true;
            try
            {
                consent.IsScreenEnabled = ScreenToggle.IsOn;
                consent.IsAudioEnabled = AudioToggle.IsOn && configuration.HasTranscriptionConfiguration;
                consent.IsObservationEnabled = ObservationToggle.IsOn && configuration.HasLlmConfiguration;
            }
            finally
            {
                applyingConsent = false;
            }
        }
        CancellationTokenSource previousCancellation;
        lock (cycleGate)
        {
            previousCancellation = cycleCancellation;
            cycleCancellation = new CancellationTokenSource();
        }
        _ = DisposeAfterCycleAsync(previousCancellation, Task.WhenAll(activeCycle, audioLifecycle));
        if (fromToggles && consent.IsObservationEnabled)
        {
            StatusText.Text = "Observation enabled. Waiting for the next 10-second cycle.";
            timer.Start();
        }
        else
        {
            timer.Stop();
            StatusText.Text = fromToggles
                ? "Observation is off. No collection or network requests."
                : "Consent changed. Observation paused; use the switches to resume.";
        }
        audioLifecycle = SynchronizeAudioAsync(audioLifecycle, cycleCancellation.Token);
    }

    private void InvalidateCycle()
    {
        Interlocked.Increment(ref consentVersion);
        lock (cycleGate)
            cycleCancellation.Cancel();
    }

    private async Task SynchronizeAudioAsync(Task previous, CancellationToken token)
    {
        await previous;
        try
        {
            if (audio is not null)
                await audio.StopAsync();
            if (token.IsCancellationRequested || IsStopping ||
                !consent.IsObservationEnabled || !consent.IsAudioEnabled)
                return;
            engine ??= createEngine();
            audio ??= createAudio();
            audio.CaptureFailed -= OnAudioCaptureFailed;
            audio.CaptureFailed += OnAudioCaptureFailed;
            await audio.StartAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation is the expected result of a consent change or shutdown.
        }
        catch (Exception error)
        {
            logger.LogWarning("Audio start failed ({ExceptionType})", error.GetType().Name);
            if (!token.IsCancellationRequested && !IsStopping)
                StatusText.Text = $"Audio unavailable ({error.GetType().Name}). Disable audio or check the playback device.";
        }
    }

    private void OnAudioCaptureFailed(object? sender, AudioCaptureFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsStopping || !ReferenceEquals(sender, audio))
                return;
            if (audio is not null)
                audio.CaptureFailed -= OnAudioCaptureFailed;
            logger.LogWarning("Audio capture failed ({ExceptionType})", args.Error.GetType().Name);
            AudioToggle.IsOn = false;
            if (consent.IsAudioEnabled)
                consent.IsAudioEnabled = false;
            StatusText.Text = $"Audio capture failed ({args.Error.GetType().Name}). Audio observation was disabled.";
        });
    }

    private async Task DisposeAfterCycleAsync(CancellationTokenSource cancellation, Task cycle)
    {
        try
        {
            await cycle;
        }
        catch (Exception error)
        {
            logger.LogWarning("Late shutdown work failed ({ExceptionType})", error.GetType().Name);
        }
        finally
        {
            lock (cycleGate)
                cancellation.Dispose();
        }
    }

    private void OnTick(object? sender, object args)
    {
        if (IsStopping || !consent.IsObservationEnabled || !activeCycle.IsCompleted || !audioLifecycle.IsCompleted)
            return;
        CancellationToken token;
        lock (cycleGate)
            token = cycleCancellation.Token;
        activeCycle = RunCycleAsync(token, Interlocked.Read(ref consentVersion));
    }

    private async Task RunCycleAsync(CancellationToken token, long version)
    {
        try
        {
            StatusText.Text = "Observing enabled sources…";
            var activeEngine = engine ??= createEngine();
            var suggestion = await Task.Run(() => activeEngine.GetSuggestionAsync(token), token);
            var body = suggestion?.Body;
            // Continuation resumes on the UI thread, so the check and UI update cannot interleave with consent changes.
            if (token.IsCancellationRequested || IsStopping ||
                version != Interlocked.Read(ref consentVersion) || !consent.IsObservationEnabled)
                return;
            SuggestionText.Text = string.IsNullOrWhiteSpace(body)
                ? "No suggestion for this context."
                : body;
            StatusText.Text = "Updated. Checking every 10 seconds while observation is enabled.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation is the expected result of a consent change or shutdown.
        }
        catch (Exception error)
        {
            logger.LogWarning("Observation cycle failed ({ExceptionType})", error.GetType().Name);
            if (!token.IsCancellationRequested && !IsStopping && version == Interlocked.Read(ref consentVersion))
            {
                StatusText.Text = $"Observation failed ({error.GetType().Name}). Check endpoint configuration, permissions, and connectivity. Retrying next cycle.";
                if (consent.IsScreenEnabled)
                    StatusText.Text += " For screen OCR, install a Windows OCR language or disable screen capture.";
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (stopTask is null)
        {
            IsStopping = true;
            ready = false;
            timer.Stop();
            timer.Tick -= OnTick;
            consent.Changed -= OnCaptureConsentChanged;
            if (audio is not null)
                audio.CaptureFailed -= OnAudioCaptureFailed;
            InvalidateCycle();
            consent.IsObservationEnabled = false;
            consent.IsScreenEnabled = false;
            consent.IsAudioEnabled = false;
            SuggestionText.Text = string.Empty;
            ObservationToggle.IsEnabled = false;
            ScreenToggle.IsEnabled = false;
            AudioToggle.IsEnabled = false;
            StatusText.Text = "Stopping capture and pending requests…";
            affordances.Dispose();
            stopTask = CompleteStopAsync();
        }
        return stopTask.WaitAsync(cancellationToken);
    }

    private async Task CompleteStopAsync()
    {
        var pending = Task.WhenAll(activeCycle, audioLifecycle);
        try
        {
            await pending.WaitAsync(ShutdownWait);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Shutdown continued with an observation cycle still running");
        }
        catch (Exception error)
        {
            logger.LogWarning("Pending shutdown work failed ({ExceptionType})", error.GetType().Name);
        }

        try
        {
            if (audio is not null)
            {
                var stoppingAudio = audio.StopAsync();
                pending = Task.WhenAll(pending, stoppingAudio);
                try
                {
                    await stoppingAudio.WaitAsync(ShutdownWait);
                }
                catch (TimeoutException)
                {
                    logger.LogWarning("Shutdown continued while audio capture was still stopping");
                }
                catch (Exception error)
                {
                    logger.LogWarning("Audio shutdown failed ({ExceptionType})", error.GetType().Name);
                }
            }
        }
        finally
        {
            if (pending.IsCompleted)
                cycleCancellation.Dispose();
            else
            {
                lateShutdownWork = pending;
                _ = DisposeAfterCycleAsync(cycleCancellation, pending);
            }
        }
    }

    private void ToggleVisibility()
    {
        if (IsStopping || !affordances.HasHotKey && !affordances.HasTray)
            return;
        if (hidden)
        {
            AppWindow.Show();
            Activate();
        }
        else
            AppWindow.Hide();
        hidden = !hidden;
    }

    private void OnHide(object sender, RoutedEventArgs args) => ToggleVisibility();
    private void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);
    private void OnExit(object sender, RoutedEventArgs args) => RequestExit();
}
