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
            if (Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is null)
                Skip = "No Windows OCR language matches the user's profile languages.";
        }
        catch (Exception error)
        {
            Skip = "Windows OCR is unavailable (" + error.GetType().Name + ").";
        }
    }
}
