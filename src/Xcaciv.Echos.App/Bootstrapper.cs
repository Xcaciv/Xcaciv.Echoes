using Microsoft.Extensions.DependencyInjection;
using Xcaciv.Echos.Capture;
using Xcaciv.Echos.Context;
using Xcaciv.Echos.Core;
using Xcaciv.Echos.Llm;

namespace Xcaciv.Echos.App;

public static class Bootstrapper
{
    public static ServiceProvider Build()
    {
        var configuration = new AppConfiguration();
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton(configuration.Llm);
        services.AddSingleton(configuration.Transcription);
        services.AddSingleton<CaptureConsent>();
        services.AddHttpClient("llm", client => client.Timeout = TimeSpan.FromSeconds(45))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient("transcription", client => client.Timeout = TimeSpan.FromSeconds(45))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<EndpointClients>();
        services.AddSingleton<IScreenCapture, DxgiScreenCapture>();
        services.AddSingleton<IOcrEngine, LazyWindowsOcrEngine>();
        services.AddSingleton<IUiInspector, UiAutomationInspector>();
        services.AddSingleton<IAudioCapture, WasapiLoopbackAudioCapture>();
        services.AddSingleton<ILlmClient>(provider => new HttpLlmClient(
            provider.GetRequiredService<EndpointClients>().Llm, configuration.Llm));
        services.AddSingleton<ITranscriptionEngine>(provider => configuration.HasTranscriptionConfiguration
            ? new WhisperTranscriptionEngine(
                provider.GetRequiredService<EndpointClients>().Transcription,
                configuration.Transcription)
            : new UnconfiguredTranscriptionEngine());
        services.AddSingleton<ContextEngine>();
        services.AddSingleton<IContextEngine>(provider => provider.GetRequiredService<ContextEngine>());
        services.AddSingleton<Func<IContextEngine>>(provider => () => provider.GetRequiredService<IContextEngine>());
        services.AddSingleton<Func<IAudioCapture>>(provider => () => provider.GetRequiredService<IAudioCapture>());
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class EndpointClients : IDisposable
    {
        public HttpClient Llm { get; }
        public HttpClient Transcription { get; }

        public EndpointClients(IHttpClientFactory factory)
        {
            Llm = factory.CreateClient("llm");
            Transcription = factory.CreateClient("transcription");
        }

        public void Dispose()
        {
            Llm.Dispose();
            Transcription.Dispose();
        }
    }

}
