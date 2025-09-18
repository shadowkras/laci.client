namespace LaciSynchroni.Utils
{
    /// <summary>
    /// Extensions for <see cref="UriBuilder"/>.
    /// </summary>
    public static class UriBuilderExtensions
    {
        public static UriBuilder WsToHttp(this UriBuilder uriBuilder)
        {
            if (string.Equals(uriBuilder.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
                uriBuilder.Scheme = Uri.UriSchemeHttps;
            else if (string.Equals(uriBuilder.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase))
                uriBuilder.Scheme = Uri.UriSchemeHttp;

            return uriBuilder;
        }

        public static UriBuilder HttpToWss(this UriBuilder uriBuilder)
        {
            if (string.Equals(uriBuilder.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                uriBuilder.Scheme = Uri.UriSchemeWss;
            else if (string.Equals(uriBuilder.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                uriBuilder.Scheme = Uri.UriSchemeWs;

            return uriBuilder;
        }
    }
}
