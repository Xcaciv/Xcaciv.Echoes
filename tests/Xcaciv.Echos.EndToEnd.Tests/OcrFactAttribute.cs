namespace Xcaciv.Echos.EndToEnd.Tests;

public sealed class OcrFactAttribute : FactAttribute
{
    public OcrFactAttribute()
    {
        Skip = DesktopRequirements.SkipReason();
        if (Skip is not null)
            return;
        try
        {
            if (Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Count == 0)
                Skip = "No Windows OCR recognizer language is installed.";
        }
        catch (Exception error)
        {
            Skip = "Windows OCR is unavailable (" + error.GetType().Name + ").";
        }
    }
}
