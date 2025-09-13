namespace LaciSynchroni.SyncConfiguration.Models;

public record TagWithServerIndex(int ServerIndex, string Tag)
{
    public string AsImGuiId()
    {
        return $"{ServerIndex}-${Tag}";
    }
}