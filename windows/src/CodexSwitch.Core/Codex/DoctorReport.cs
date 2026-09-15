using System.Text.Json;

namespace CodexSwitch.Core.Codex;

public static class DoctorReport
{
    public static bool IsConfigValid(string standardOutput)
    {
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            return document.RootElement
                       .GetProperty("checks")
                       .GetProperty("config.load")
                       .GetProperty("status")
                       .GetString() == "ok";
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }
}
