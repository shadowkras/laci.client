namespace SinusSynchronous.SinusConfiguration.Models
{
    public record TagWithServerIndex(int ServerIndex, string Tag)
    {
        public string AsImgUiId()
        {
            return $"{ServerIndex}-${Tag}";
        }
    }
}