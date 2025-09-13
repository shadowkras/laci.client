namespace SinusSynchronous.SinusConfiguration.Models
{
    public record TagWithServerIndex(int ServerIndex, string Tag)
    {
        public string AsImGuiId()
        {
            return $"{ServerIndex}-${Tag}";
        }
    }
}