using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;
using GitHub.Services.Common;
using GitHub.Services.OAuth;

namespace GitHub.Runner.Listener.Configuration
{
    // TODO: Refactor extension manager to enable using it from the agent process.
    [ServiceLocator(Default = typeof(CredentialManager))]
    public interface ICredentialManager : IRunnerService
    {
        ICredentialProvider GetCredentialProvider(string credType);
        VssCredentials LoadCredentials(int? runnerId = null);
    }

    public class CredentialManager : RunnerService, ICredentialManager
    {
        public static readonly Dictionary<string, Type> CredentialTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            { Constants.Configuration.OAuth, typeof(OAuthCredential)},
            { Constants.Configuration.OAuthAccessToken, typeof(OAuthAccessTokenCredential)},
        };

        public ICredentialProvider GetCredentialProvider(string credType)
        {
            Trace.Info(nameof(GetCredentialProvider));
            Trace.Info("Creating type {0}", credType);

            if (!CredentialTypes.ContainsKey(credType))
            {
                throw new ArgumentException("Invalid Credential Type");
            }

            Trace.Info("Creating credential type: {0}", credType);
            var creds = Activator.CreateInstance(CredentialTypes[credType]) as ICredentialProvider;
            Trace.Verbose("Created credential type");
            return creds;
        }

        public VssCredentials LoadCredentials(int? runnerId)
        {
            IConfigurationStore store = HostContext.GetService<IConfigurationStore>();

            if (!store.HasCredentials(runnerId))
            {
                throw new InvalidOperationException("Credentials not stored.  Must reconfigure.");
            }

            CredentialData credData = store.GetCredentials(runnerId);
            var migratedCred = store.GetMigratedCredentials(runnerId);
            if (migratedCred != null)
            {
                credData = migratedCred;

                // Re-write .credentials with Token URL
                store.SaveCredential(credData, runnerId);

                // Delete .credentials_migrated
                store.DeleteMigratedCredential(runnerId);
            }

            ICredentialProvider credProv = GetCredentialProvider(credData.Scheme);
            credProv.CredentialData = credData;

            VssCredentials creds = credProv.GetVssCredentials(HostContext, runnerId);

            return creds;
        }
    }

    [DataContract]
    public sealed class GitHubRunnerRegisterToken
    {
        [DataMember(Name = "token")]
        public string Token { get; set; }

        [DataMember(Name = "expires_at")]
        public string ExpiresAt { get; set; }
    }

    [DataContract]
    public sealed class GitHubAuthResult
    {
        [DataMember(Name = "url")]
        public string TenantUrl { get; set; }

        [DataMember(Name = "token_schema")]
        public string TokenSchema { get; set; }

        [DataMember(Name = "token")]
        public string Token { get; set; }

        public VssCredentials ToVssCredentials()
        {
            ArgUtil.NotNullOrEmpty(TokenSchema, nameof(TokenSchema));
            ArgUtil.NotNullOrEmpty(Token, nameof(Token));

            if (string.Equals(TokenSchema, "OAuthAccessToken", StringComparison.OrdinalIgnoreCase))
            {
                return new VssCredentials(new VssOAuthAccessTokenCredential(Token), CredentialPromptType.DoNotPrompt);
            }
            else
            {
                throw new NotSupportedException($"Not supported token schema: {TokenSchema}");
            }
        }
    }
}
