namespace LaciSynchroni.Utils
{
    /// <summary>
    /// Extensions for <see cref="UriBuilder"/>.
    /// </summary>
    public static class UriBuilderExtensions
    {
        /// <summary>
        /// Convert ws/wss scheme to http/https.
        /// </summary>
        /// <returns></returns>
        public static UriBuilder WsToHttp(this UriBuilder uriBuilder)
        {
            if (string.Equals(uriBuilder.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
                uriBuilder.Scheme = Uri.UriSchemeHttps;
            else if (string.Equals(uriBuilder.Scheme, Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase))
                uriBuilder.Scheme = Uri.UriSchemeHttp;

            return uriBuilder;
        }

        /// <summary>
        /// Convert http/https scheme to ws/wss.
        /// </summary>
        /// <returns></returns>
        public static UriBuilder HttpToWs(this UriBuilder uriBuilder)
        {
            if (string.Equals(uriBuilder.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                uriBuilder.Scheme = Uri.UriSchemeWss;
            else if (string.Equals(uriBuilder.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                uriBuilder.Scheme = Uri.UriSchemeWs;

            return uriBuilder;
        }
    }
}
