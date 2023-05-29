using GitHub.Runner.Common.Util;
using GitHub.Runner.Sdk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace GitHub.Runner.Common
{
    //
    // Settings are persisted in this structure
    //
    [DataContract]
    public sealed class RunnerSettings
    {
        [DataMember(Name = "IsHostedServer", EmitDefaultValue = false)]
        private bool? _isHostedServer;

        [DataMember(EmitDefaultValue = false)]
        public int AgentId { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string AgentName { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public bool SkipSessionRecover { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public int PoolId { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string PoolName { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string ServerUrl { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string GitHubUrl { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string WorkFolder { get; set; }

        [DataMember(EmitDefaultValue = false)]
        public string MonitorSocketAddress { get; set; }

        [DataMember(Name = "PullRequestSecurity", EmitDefaultValue = false)]
        public PullRequestSecuritySettings PullRequestSecuritySettings { get; set; }

        [IgnoreDataMember]
        public bool IsHostedServer
        {
            get
            {
                // Old runners do not have this property. Hosted runners likely don't have this property either.
                return _isHostedServer ?? true;
            }

            set
            {
                _isHostedServer = value;
            }
        }

        /// <summary>
        // Computed property for convenience. Can either return:
        // 1. If runner was configured at the repo level, returns something like: "myorg/myrepo"
        // 2. If runner was configured at the org level, returns something like: "myorg"
        /// </summary>
        public string RepoOrOrgName
        {
            get
            {
                Uri accountUri = new Uri(this.ServerUrl);
                string repoOrOrgName = string.Empty;

                if (accountUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                {
                    Uri gitHubUrl = new Uri(this.GitHubUrl);

                    // Use the "NWO part" from the GitHub URL path
                    repoOrOrgName = gitHubUrl.AbsolutePath.Trim('/');
                }
                else
                {
                    repoOrOrgName = accountUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                }

                return repoOrOrgName;
            }
        }

        [OnSerializing]
        private void OnSerializing(StreamingContext context)
        {
            if (_isHostedServer.HasValue && _isHostedServer.Value)
            {
                _isHostedServer = null;
            }
        }
    }

    [DataContract]
    public sealed class PullRequestSecuritySettings
    {
        // pullRequestSecurity is optional in the config -- if the key is
        // defined, assume that we only want collaborators to run PRs.
        [DataMember(EmitDefaultValue = false)]
        public HashSet<string> AllowedAuthors = new HashSet<string>();

        [DataMember(EmitDefaultValue = false)]
        public bool AllowContributors = true;
    }

    [ServiceLocator(Default = typeof(ConfigurationStore))]
    public interface IConfigurationStore : IRunnerService
    {
        bool IsConfigured();
        bool IsConfigured(int? runnerId);
        bool IsServiceConfigured(int? runnerId = null);
        bool HasCredentials(int? runnerId = null);
        CredentialData GetCredentials(int? runnerId = null);
        CredentialData GetMigratedCredentials(int? runnerId = null);
        RunnerSettings GetSettings(int? runnerId = null);
        void SaveCredential(CredentialData credential, int? runnerId = null);
        void SaveSettings(RunnerSettings settings, int? runnerId = null);
        void DeleteCredential(int? runnerId = null);
        void DeleteMigratedCredential(int? runnerId = null);
        void DeleteSettings(int? runnerId = null);
    }

    public sealed class ConfigurationStore : RunnerService, IConfigurationStore
    {
        private string _binPath;
        private Func<int?, string> _configFilePath;
        private Func<int?, string> _credFilePath;
        private Func<int?, string> _migratedCredFilePath;
        private Func<int?, string> _serviceConfigFilePath;

        private CredentialData[] _creds;
        private CredentialData[] _migratedCreds;
        private RunnerSettings[] _settings;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);

            _creds = new CredentialData[Constants.AvailableRunnerInstances];
            _migratedCreds = new CredentialData[Constants.AvailableRunnerInstances];
            _settings = new RunnerSettings[Constants.AvailableRunnerInstances];

            var credentialsDir = HostContext.GetDirectory(WellKnownDirectory.ConfigDir);
            Directory.CreateDirectory(credentialsDir);

            var currentAssemblyLocation = System.Reflection.Assembly.GetEntryAssembly().Location;
            Trace.Info("currentAssemblyLocation: {0}", currentAssemblyLocation);

            _binPath = HostContext.GetDirectory(WellKnownDirectory.Bin);
            Trace.Info("binPath: {0}", _binPath);

            RootFolder = HostContext.GetDirectory(WellKnownDirectory.Root);
            Trace.Info("RootFolder: {0}", RootFolder);

            _configFilePath = (int? id) => hostContext.GetConfigFile(WellKnownConfigFile.Runner, id);
            Trace.Info("ConfigFilePath: {0}", _configFilePath);

            _credFilePath = (int? id) => hostContext.GetConfigFile(WellKnownConfigFile.Credentials, id);
            Trace.Info("CredFilePath: {0}", _credFilePath);

            _migratedCredFilePath = (int? id) => hostContext.GetConfigFile(WellKnownConfigFile.MigratedCredentials, id);
            Trace.Info("MigratedCredFilePath: {0}", _migratedCredFilePath);

            _serviceConfigFilePath = (int? id) => hostContext.GetConfigFile(WellKnownConfigFile.Service, id);
            Trace.Info("ServiceConfigFilePath: {0}", _serviceConfigFilePath);
        }

        public string RootFolder { get; private set; }

        public bool HasCredentials(int? runnerId)
        {
            Trace.Info("HasCredentials()");
            bool credsStored = (new FileInfo(_credFilePath(runnerId))).Exists || (new FileInfo(_migratedCredFilePath(runnerId))).Exists;
            Trace.Info("stored {0}", credsStored);
            return credsStored;
        }

        public bool IsConfigured()
        {
            Trace.Info("IsConfigured()");
            bool configured = true;
            for (int i = 0; i < Constants.AvailableRunnerInstances; ++i)
                configured &= new FileInfo(_configFilePath(i)).Exists;
            Trace.Info("IsConfigured: {0}", configured);
            return configured;
        }

        public bool IsConfigured(int? runnerId)
        {
            Trace.Info("IsConfigured(int runnerId)");
            bool configured = new FileInfo(_configFilePath(runnerId)).Exists;
            Trace.Info("IsConfigured: {0}", configured);
            return configured;
        }

        public bool IsServiceConfigured(int? runnerId)
        {
            Trace.Info("IsServiceConfigured()");
            bool serviceConfigured = (new FileInfo(_serviceConfigFilePath(runnerId))).Exists;
            Trace.Info($"IsServiceConfigured: {serviceConfigured}");
            return serviceConfigured;
        }

        private int ValidateRunnerId(int? runnerId)
        {
            return (runnerId == null) ? int.Parse(Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable)) : (int) runnerId;
        }

        public CredentialData GetCredentials(int? runnerId)
        {
            int id = ValidateRunnerId(runnerId);
            if (_creds[id] == null)
            {
                _creds[id] = IOUtil.LoadObject<CredentialData>(_credFilePath(runnerId));
            }

            return _creds[id];
        }

        public CredentialData GetMigratedCredentials(int? runnerId)
        {
            int id = ValidateRunnerId(runnerId);
            if (_migratedCreds[id] == null && File.Exists(_migratedCredFilePath(runnerId)))
            {
                _migratedCreds[id] = IOUtil.LoadObject<CredentialData>(_migratedCredFilePath(runnerId));
            }

            return _migratedCreds[id];
        }

        public RunnerSettings GetSettings(int? runnerId)
        {
            int id = ValidateRunnerId(runnerId);
            if (_settings[id] == null)
            {
                RunnerSettings configuredSettings = null;
                if (File.Exists(_configFilePath(runnerId)))
                {
                    string json = File.ReadAllText(_configFilePath(runnerId), Encoding.UTF8);
                    Trace.Info($"Read setting file: {json.Length} chars");
                    configuredSettings = StringUtil.ConvertFromJson<RunnerSettings>(json);
                }

                ArgUtil.NotNull(configuredSettings, nameof(configuredSettings));
                _settings[id] = configuredSettings;
            }

            return _settings[id];
        }

        public void SaveCredential(CredentialData credential, int? runnerId)
        {
            Trace.Info("Saving {0} credential @ {1}", credential.Scheme, _credFilePath);
            if (File.Exists(_credFilePath(runnerId)))
            {
                // Delete existing credential file first, since the file is hidden and not able to overwrite.
                Trace.Info("Delete exist runner credential file.");
                IOUtil.DeleteFile(_credFilePath(runnerId));
            }

            IOUtil.SaveObject(credential, _credFilePath(runnerId));
            Trace.Info("Credentials Saved.");
            File.SetAttributes(_credFilePath(runnerId), File.GetAttributes(_credFilePath(runnerId)) | FileAttributes.Hidden);
        }

        public void SaveSettings(RunnerSettings settings, int? runnerId)
        {
            Trace.Info("Saving runner settings.");
            if (File.Exists(_configFilePath(runnerId)))
            {
                // Delete existing runner settings file first, since the file is hidden and not able to overwrite.
                Trace.Info("Delete exist runner settings file.");
                IOUtil.DeleteFile(_configFilePath(runnerId));
            }

            IOUtil.SaveObject(settings, _configFilePath(runnerId));
            Trace.Info("Settings Saved.");
            File.SetAttributes(_configFilePath(runnerId), File.GetAttributes(_configFilePath(runnerId)) | FileAttributes.Hidden);
        }

        public void DeleteCredential(int? runnerId)
        {
            IOUtil.Delete(_credFilePath(runnerId), default(CancellationToken));
            IOUtil.Delete(_migratedCredFilePath(runnerId), default(CancellationToken));
        }

        public void DeleteMigratedCredential(int? runnerId)
        {
            IOUtil.Delete(_migratedCredFilePath(runnerId), default(CancellationToken));
        }

        public void DeleteSettings(int? runnerId)
        {
            IOUtil.Delete(_configFilePath(runnerId), default(CancellationToken));
        }
    }
}
