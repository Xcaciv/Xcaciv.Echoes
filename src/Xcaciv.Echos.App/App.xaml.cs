using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace Xcaciv.Echos.App;

public sealed partial class App : Application
{
    private ServiceProvider? services;
    private MainWindow? window;
    private bool shutdownComplete;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        services = Bootstrapper.Build();
        window = services.GetRequiredService<MainWindow>();
        window.AppWindow.Closing += OnWindowClosing;
        window.ExitRequested += OnExitRequested;
        window.Activate();
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (shutdownComplete)
            return;
        args.Cancel = true;
        _ = ShutdownAsync();
    }

    private void OnExitRequested(object? sender, EventArgs args) => _ = ShutdownAsync();

    private async Task ShutdownAsync()
    {
        if (window is null || window.IsStopping)
            return;
        try
        {
            try
            {
                await window.StopAsync();
            }
            finally
            {
                if (services is not null)
                    await services.DisposeAsync();
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine($"Echos shutdown: {error.GetType().Name}");
        }
        finally
        {
            services = null;
            shutdownComplete = true;
            window.Close();
            window = null;
        }
    }
}
