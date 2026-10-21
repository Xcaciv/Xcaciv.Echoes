namespace Xcaciv.Echos.Core;

public interface IInputMonitor
{
    event EventHandler<KeyEventArgs>? KeyPressed;
    event EventHandler<MouseEventArgs>? MouseActivity;
    void Start();
    void Stop();
}
