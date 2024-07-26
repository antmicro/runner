using System;

namespace GitHub.Runner.Sdk
{
    public static class UrlUtil
    {
        // A dummy method to avoid pulling https://github.com/actions/runner/commit/7ee333b5cd75edaeafa2cd57089be2d5a67d6f04
        public static bool IsHostedServer(UriBuilder gitHubUrl)
        {
            return true;
        }

        public static Uri GetCredentialEmbeddedUrl(Uri baseUrl, string username, string password)
        {
            ArgUtil.NotNull(baseUrl, nameof(baseUrl));

            // return baseurl when there is no username and password
            if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
            {
                return baseUrl;
            }

            UriBuilder credUri = new UriBuilder(baseUrl);

            // ensure we have a username, uribuild will throw if username is empty but password is not.
            if (string.IsNullOrEmpty(username))
            {
                username = "emptyusername";
            }

            // escape chars in username for uri
            credUri.UserName = Uri.EscapeDataString(username);

            // escape chars in password for uri
            if (!string.IsNullOrEmpty(password))
            {
                credUri.Password = Uri.EscapeDataString(password);
            }

            return credUri.Uri;
        }
    }
}
