using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Xcaciv.Echos.Core;

namespace Xcaciv.Echos.App;

public sealed partial class MainWindow : Window
{
    private readonly CaptureConsent consent;
    private readonly AppConfiguration configuration;
    private readonly Func<IContextEngine> createEngine;
    private readonly Func<IAudioCapture> createAudio;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly WindowAffordances affordances;
    private CancellationTokenSource cycleCancellation = new();
    private Task activeCycle = Task.CompletedTask;
    private Task audioLifecycle = Task.CompletedTask;
    private IContextEngine? engine;
    private IAudioCapture? audio;
    private bool ready;
    private bool hidden;
    private bool applyingConsent;
    private long consentVersion;

    public bool IsStopping { get; private set; }
    public event EventHandler? ExitRequested;

    public MainWindow(CaptureConsent consent, AppConfiguration configuration, Func<IContextEngine> createEngine,
        Func<IAudioCapture> createAudio)
    {
        this.consent = consent;
        this.configuration = configuration;
        this.createEngine = createEngine;
        this.createAudio = createAudio;
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

    private void OnConsentChanged(object sender, RoutedEventArgs args)
    {
        if (!ready || IsStopping)
            return;
        var previousCancellation = cycleCancellation;
        previousCancellation.Cancel();
        SuggestionText.Text = string.Empty;

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
        cycleCancellation = new CancellationTokenSource();
        _ = DisposeAfterCycleAsync(previousCancellation, Task.WhenAll(activeCycle, audioLifecycle));
        if (consent.IsObservationEnabled)
        {
            StatusText.Text = "Observation enabled. Waiting for the next 10-second cycle.";
            timer.Start();
        }
        else
        {
            timer.Stop();
            StatusText.Text = "Observation is off. No collection or network requests.";
        }
        audioLifecycle = SynchronizeAudioAsync(audioLifecycle, cycleCancellation.Token);
    }

    private void OnCaptureConsentChanged(object? sender, EventArgs args)
    {
        var version = Interlocked.Increment(ref consentVersion);
        try
        {
            Volatile.Read(ref cycleCancellation).Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        void ClearRevokedContext()
        {
            if (IsStopping || Interlocked.Read(ref consentVersion) != version)
                return;
            SuggestionText.Text = string.Empty;
            if (!applyingConsent)
            {
                cycleCancellation.Cancel();
                timer.Stop();
                StatusText.Text = "Consent changed. Observation paused; use the switches to resume.";
                audioLifecycle = SynchronizeAudioAsync(audioLifecycle, cycleCancellation.Token);
            }
        }
        if (DispatcherQueue.HasThreadAccess)
            ClearRevokedContext();
        else
            DispatcherQueue.TryEnqueue(ClearRevokedContext);
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
            await audio.StartAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && !IsStopping)
                StatusText.Text = $"Audio unavailable ({error.GetType().Name}). Disable audio or check the playback device.";
        }
    }

    private static async Task DisposeAfterCycleAsync(CancellationTokenSource cancellation, Task cycle)
    {
        try
        {
            await cycle;
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void OnTick(object? sender, object args)
    {
        if (IsStopping || !consent.IsObservationEnabled || !activeCycle.IsCompleted || !audioLifecycle.IsCompleted)
            return;
        activeCycle = RunCycleAsync(cycleCancellation.Token, Interlocked.Read(ref consentVersion));
    }

    private async Task RunCycleAsync(CancellationToken token, long version)
    {
        try
        {
            StatusText.Text = "Observing enabled sources…";
            var activeEngine = engine ??= createEngine();
            var suggestion = await Task.Run(() => activeEngine.GetSuggestionAsync(token), token);
            var body = suggestion?.Body;
            lock (consent)
            {
                if (token.IsCancellationRequested || IsStopping ||
                    version != Interlocked.Read(ref consentVersion) || !consent.IsObservationEnabled)
                    return;
                SuggestionText.Text = string.IsNullOrWhiteSpace(body)
                    ? "No suggestion for this context."
                    : body;
                StatusText.Text = "Updated. Checking every 10 seconds while observation is enabled.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && !IsStopping && version == Interlocked.Read(ref consentVersion))
            {
                StatusText.Text = $"Observation failed ({error.GetType().Name}). Check endpoint configuration, permissions, and connectivity. Retrying next cycle.";
                if (consent.IsScreenEnabled)
                    StatusText.Text += " For screen OCR, install a Windows OCR language or disable screen capture.";
            }
        }
    }

    public async Task StopAsync()
    {
        if (IsStopping)
            return;
        IsStopping = true;
        ready = false;
        timer.Stop();
        timer.Tick -= OnTick;
        consent.Changed -= OnCaptureConsentChanged;
        cycleCancellation.Cancel();
        consent.IsObservationEnabled = false;
        consent.IsScreenEnabled = false;
        consent.IsAudioEnabled = false;
        SuggestionText.Text = string.Empty;
        ObservationToggle.IsEnabled = false;
        ScreenToggle.IsEnabled = false;
        AudioToggle.IsEnabled = false;
        StatusText.Text = "Stopping capture and pending requests…";
        affordances.Dispose();
        await Task.WhenAll(activeCycle, audioLifecycle);
        if (audio is not null)
            await audio.StopAsync();
        cycleCancellation.Dispose();
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
