using GitHub.DistributedTask.WebApi;
using Pipelines = GitHub.DistributedTask.Pipelines;
using GitHub.Runner.Common.Util;
using GitHub.Services.Common;
using GitHub.Services.WebApi;
using GitHub.Actions.Pipelines.WebApi;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using GitHub.DistributedTask.Pipelines;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;
using GitHub.Runner.GCP;
using GitHub.DistributedTask.Pipelines.ContextData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GitHub.Runner.Worker
{
    [ServiceLocator(Default = typeof(JobRunner))]
    public interface IJobRunner : IRunnerService
    {
        Task<TaskResult> RunAsync(AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken);
    }

    public sealed class JobRunner : RunnerService, IJobRunner
    {
        private IJobServerQueue _jobServerQueue;
        private ITempDirectoryManager _tempDirectoryManager;
        private const string RestrictedServiceAccountWarning = "Attachment of SA is restricted to non-fork PRs";
        private IBesServerHttpClient _besServerClient;

        public async Task<TaskResult> RunAsync(AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken)
        {
            // Validate parameters.
            Trace.Entering();
            ArgUtil.NotNull(message, nameof(message));
            ArgUtil.NotNull(message.Resources, nameof(message.Resources));
            ArgUtil.NotNull(message.Variables, nameof(message.Variables));
            ArgUtil.NotNull(message.Steps, nameof(message.Steps));
            Trace.Info("Job ID {0}", message.JobId);

            DateTime jobStartTimeUtc = DateTime.UtcNow;
            IRunnerService server = null;

            ServiceEndpoint systemConnection = message.Resources.Endpoints.Single(x => string.Equals(x.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
            if (MessageUtil.IsRunServiceJob(message.MessageType))
            {
                var runServer = HostContext.GetService<IRunServer>();
                VssCredentials jobServerCredential = VssUtil.GetVssCredential(systemConnection);
                await runServer.ConnectAsync(systemConnection.Url, jobServerCredential);
                server = runServer;
            }
            else
            {
                // Setup the job server and job server queue.
                var jobServer = HostContext.GetService<IJobServer>();
                VssCredentials jobServerCredential = VssUtil.GetVssCredential(systemConnection);
                Uri jobServerUrl = systemConnection.Url;

                Trace.Info($"Creating job server with URL: {jobServerUrl}");
                // jobServerQueue is the throttling reporter.
                _jobServerQueue = HostContext.GetService<IJobServerQueue>();
                VssConnection jobConnection = VssUtil.CreateConnection(jobServerUrl, jobServerCredential, new DelegatingHandler[] { new ThrottlingReportHandler(_jobServerQueue) });
                await jobServer.ConnectAsync(jobConnection);

                _jobServerQueue.Start(message);
                server = jobServer;
            }


            // Spawn Google VM
            var instanceNumber = Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable);
            var rootDir = new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Root)).Parent.FullName;
            var virtDir = HostContext.GetDirectory(WellKnownDirectory.Virt);

            Trace.Info($"Runner instance: {instanceNumber}");

            Trace.Info($"QEMU tools directory: {virtDir}");

            Trace.Info($"Job container: {message.JobContainer}");

            var repoFullName = $"{message.ContextData["github"].ToJToken()["repository"]}";
            var repoName = repoFullName.Substring(repoFullName.LastIndexOf('/') + 1);
            Trace.Info($"Full repo name: {repoFullName}");
            Trace.Info($"Repo name: {repoName}");

            dynamic vmSpecs = JObject.Parse(File.ReadAllText(Path.Combine(rootDir, ".vm_specs.json")));

            //Trace.Info("Creating pipeline server");
            //_pipelinesHttpClient = jobConnection.GetClient<PipelinesHttpClient>();

            HostContext.WritePerfCounter($"WorkerJobServerQueueStarted_{message.RequestId.ToString()}");

            IExecutionContext jobContext = null;
            CancellationTokenRegistration? runnerShutdownRegistration = null;
            try
            {
                // Create the job execution context.
                jobContext = HostContext.CreateService<IExecutionContext>();
                jobContext.InitializeJob(message, jobRequestCancellationToken);

                var PipelineDirectory = repoName.ToString(CultureInfo.InvariantCulture);
                string WorkspaceDirectory = Path.Combine(PipelineDirectory, repoName);
                Trace.Info($"Validating directory permissions for: '{WorkspaceDirectory}'");
                try
                {
                    Directory.CreateDirectory(WorkspaceDirectory);
                    IOUtil.ValidateExecutePermission(WorkspaceDirectory);
                }
                catch (Exception ex)
                {
                    Trace.Error(ex);
                    jobContext.Error(ex);
                    return await CompleteJobAsync(server, jobContext, message, TaskResult.Failed);
                }

                Trace.Info($"PipelineDirectory: {PipelineDirectory}");
                Trace.Info($"WorkspaceDirectory: {WorkspaceDirectory}");

                message.Variables["system.qemuDir"] = virtDir;
                message.Variables["system.containerWorkspace"] = WorkspaceDirectory;

                string messageSerialized = JsonConvert.SerializeObject(message);
                JObject messageJson = JObject.Parse(messageSerialized);
                Trace.Info("Starting the job execution context.");
                jobContext.Start();
                var githubContext = jobContext.ExpressionValues["github"] as GitHubContext;

                _besServerClient = HostContext.GetService<IBesServerHttpClient>();
                Trace.Info("BES: Creating invocation");
                await _besServerClient.CreateInvocation(githubContext);
                await _besServerClient.AddRunInformation(githubContext);
                await _besServerClient.AddTarget(githubContext, message.Variables["system.github.token"].Value);

                var templateEval = jobContext.ToPipelineTemplateEvaluator();
                var container = templateEval.EvaluateJobContainer(message.JobContainer, jobContext.ExpressionValues, jobContext.ExpressionFunctions);

                // Interpret special variables.
                var externalDisk = String.Empty;
                var preemptibleOverride = String.Empty;
                var machineType = String.Empty;
                var serviceAccount = String.Empty;
                var tunnelConfig = String.Empty;
                var tunnelConfigGcp = String.Empty;
                var tunnelKey = String.Empty; 
                var tunnelKeyGcp = String.Empty;

                // The following sorcery is done so that templating in environment variables works properly.
                foreach (var token in message.EnvironmentVariables)
                {
                    var environmentVariables = templateEval.EvaluateStepEnvironment(token, 
                            jobContext.ExpressionValues, 
                            jobContext.ExpressionFunctions, 
                            VarUtil.EnvironmentVariableKeyComparer);

                    foreach (var pair in environmentVariables)
                    {
                        var val = pair.Value ?? string.Empty;

                        switch (pair.Key)
                        {
                            case "GHA_EXTERNAL_DISK":
                                Trace.Info("External disk variable is present.");

                                externalDisk = val;
                                break;
                            case "GHA_PREEMPTIBLE":
                                Trace.Info("Preemptible override variable is present.");

                                preemptibleOverride = val;
                                break;
                            case "GHA_MACHINE_TYPE":
                                Trace.Info("Machine type variable is present.");

                                machineType = val;
                                break;
                            case "GHA_SA":
                                Trace.Info("SA variable is present.");

                                serviceAccount = val;
                                break;
                            case "GHA_SSH_TUNNEL_CONFIG":
                                Trace.Info("Tunnel config was provided.");

                                if (DecodeBase64OrAddIssue(pair, jobContext))
                                {
                                    tunnelConfig = val;
                                }
                                break;
                            case "GHA_SSH_TUNNEL_CONFIG_SECRET_NAME":
                                Trace.Info("Tunnel config will be taken from GCP Secret Manager");

                                tunnelConfigGcp = val;
                                break;
                            case "GHA_SSH_TUNNEL_KEY":
                                Trace.Info("Tunnel key was provided.");

                                if (DecodeBase64OrAddIssue(pair, jobContext))
                                {
                                    tunnelKey = val;
                                }
                                break;
                            case "GHA_SSH_TUNNEL_KEY_SECRET_NAME":
                                Trace.Info("Tunnel key will be taken from GCP Secret Manager");

                                tunnelKeyGcp = val;
                                break;
                            case "GHA_CUSTOM_LINE_PREFIX":
                                Trace.Info("Custom line prefix for logs was provided");

                                jobContext.CustomPrefix = val;
                                break;
                            default:
                                Trace.Info($"Ignoring variable {pair.Key}");
                                break;
                        }
                    }
                }

                Trace.Info($"External disk: {externalDisk}; Preemptible override: {preemptibleOverride}; Machine type: {machineType}");

                if (!JobPassesSecurityRestrictions(jobContext))
                {
                    jobContext.Error("Running job on this worker disallowed by security policy");
                    return await CompleteJobAsync(server, jobContext, message, TaskResult.Failed);
                }

                IExecutionContext vmCtx = jobContext.CreateChild(Guid.NewGuid(), "Set up VM", "VM_Init", null, null, ActionRunStage.Main);
                vmCtx.Start();
                if (_besServerClient.LastInvocationId != null)
                    vmCtx.Output($"build-results-viewer invocation ID of this run: {_besServerClient.LastInvocationId}");

                Trace.Info($"Container: ${container.Image}");

                var spawnMachineArgs = $"vm_command.py --mode create_vm -n {instanceNumber} -s {container.Image}";

                if (!String.IsNullOrEmpty(tunnelConfigGcp) && !String.IsNullOrEmpty(tunnelKeyGcp))
                {
                    tunnelConfig = GCPCoordinator.GetGcpSecret(HostContext, tunnelConfigGcp, repoName);
                    tunnelKey = GCPCoordinator.GetGcpSecret(HostContext, tunnelKeyGcp, repoName);
                }

                if (!String.IsNullOrEmpty(tunnelConfig) && !String.IsNullOrEmpty(tunnelKey))
                {
                    spawnMachineArgs += $" --ssh-tunnel-config {tunnelConfig} --ssh-tunnel-key {tunnelKey}";
                }

                if (!String.IsNullOrEmpty(externalDisk))
                {
                    spawnMachineArgs += $" -d {externalDisk}";
                }

                if (!String.IsNullOrEmpty(machineType))
                {
                    spawnMachineArgs += $" -m {machineType}";
                }

                if (!String.IsNullOrEmpty(serviceAccount))
                {
                    try {
                        // Allow to attach SA only to PRs not from fork
                        if (IsPullRequestFromFork(githubContext)) {
                            jobContext.Warning(RestrictedServiceAccountWarning);
                            vmCtx.Output(RestrictedServiceAccountWarning);
                        } else {
                            spawnMachineArgs += $" -a {serviceAccount}";

                            // set additional environment variables changing default Google metadata server to IP
                            // GCE_METADATA_HOST is the newer name for the environment variable,
                            // but some applications still uses GCE_METADATA_ROOT
                            if (!jobContext.Global.EnvironmentVariables.ContainsKey("GCE_METADATA_HOST"))
                                jobContext.Global.EnvironmentVariables.Add("GCE_METADATA_HOST", "169.254.169.254");
                            if (!jobContext.Global.EnvironmentVariables.ContainsKey("GCE_METADATA_ROOT"))
                                jobContext.Global.EnvironmentVariables.Add("GCE_METADATA_ROOT", "169.254.169.254");
                        }
                    } catch(Exception e) {
                        Trace.Error("Exception when checking if PR is from fork!");
                        Trace.Error(e.Message);
                        jobContext.Error("Exception when starting job!");
                        return await CompleteJobAsync(server, jobContext, message, TaskResult.Failed);
                    }
                }

                if (!String.IsNullOrEmpty(preemptibleOverride))
                {
                    bool preemptibleOverrideBool;

                    if (Boolean.TryParse(preemptibleOverride, out preemptibleOverrideBool))
                    {
                        spawnMachineArgs += $" -p {Convert.ToInt32(preemptibleOverrideBool)}";
                    }
                    else
                    {
                        vmCtx.Output("Boolean value expected for preemptible override.");
                    }
                }

                int vmExitCode = 1;
                for (int i = 0; i < 5; i++) {
                    StringBuilder output_string = new StringBuilder();
                    vmExitCode = StartGcpMachine(spawnMachineArgs, virtDir, output_string, vmCtx, jobContext);
                    if(vmExitCode > 0) {
                        RestartGcpMachine(vmExitCode, jobContext, message, vmSpecs, ref vmCtx);
                        continue;
                    }

                    if (!SetRunnerIP(output_string.ToString())) { 
                        vmExitCode = 1;
                        RestartGcpMachine(vmExitCode, jobContext, message, vmSpecs, ref vmCtx);
                        continue;
                    }
                    if (vmExitCode == 0)
                        break;
                }
                // after 5 unsuccessful attempts to start VM, fail whole job
                if (vmExitCode > 0) {
                    jobContext.Error($"VM starter exited with non-zero exit code: {vmExitCode}");
                    vmCtx.Complete();
                    return await CompleteJobAsync(server, jobContext, message, TaskResult.Failed);
                }

                // get and set bucket name for logs
                HostContext.BucketName = GCPCoordinator.GetGcpBucketName(HostContext);

                // Setup TEMP directories
                _tempDirectoryManager = HostContext.GetService<ITempDirectoryManager>();
                _tempDirectoryManager.InitializeTempDirectory(jobContext);

                vmCtx.Complete();

                jobContext.Debug($"Starting: {message.JobDisplayName}");

                runnerShutdownRegistration = HostContext.RunnerShutdownToken.Register(() =>
                {
                    // log an issue, then runner get shutdown by Ctrl-C or Ctrl-Break.
                    // the server will use Ctrl-Break to tells the runner that operating system is shutting down.
                    string errorMessage;
                    switch (HostContext.RunnerShutdownReason)
                    {
                        case ShutdownReason.UserCancelled:
                            errorMessage = "The runner has received a shutdown signal. This can happen when the runner service is stopped, or a manually started runner is canceled.";
                            break;
                        case ShutdownReason.OperatingSystemShutdown:
                            errorMessage = $"Operating system is shutting down for computer '{Environment.MachineName}'";
                            break;
                        default:
                            throw new ArgumentException(HostContext.RunnerShutdownReason.ToString(), nameof(HostContext.RunnerShutdownReason));
                    }
                    FinalizeGcp(jobContext, message, vmSpecs);
                    jobContext.AddIssue(new Issue() { Type = IssueType.Error, Message = errorMessage });
                });

                // Validate directory permissions.
                string workDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
                Trace.Info($"Validating directory permissions for: '{workDirectory}'");
                try
                {
                    Directory.CreateDirectory(workDirectory);
                    IOUtil.ValidateExecutePermission(workDirectory);
                }
                catch (Exception ex)
                {
                    Trace.Error(ex);
                    jobContext.Error(ex);
                    return await CompleteJobAsync(server, jobContext, message, TaskResult.Failed);

                }

                if (jobContext.Global.WriteDebug)
                {
                    jobContext.SetRunnerContext("debug", "1");
                }

                jobContext.SetRunnerContext("os", VarUtil.OS);

                string toolsDirectory = HostContext.GetDirectory(WellKnownDirectory.Tools);
                Directory.CreateDirectory(toolsDirectory);
                jobContext.SetRunnerContext("tool_cache", toolsDirectory);

                // Get the job extension.
                Trace.Info("Getting job extension.");
                IJobExtension jobExtension = HostContext.CreateService<IJobExtension>();
                List<IStep> jobSteps = null;
                try
                {
                    Trace.Info("Initialize job. Getting all job steps.");
                    jobSteps = await jobExtension.InitializeJob(jobContext, message);
                }
                catch (OperationCanceledException ex) when (jobContext.CancellationToken.IsCancellationRequested)
                {
                    // set the job to canceled
                    // don't log error issue to job ExecutionContext, since server owns the job level issue
                    Trace.Error($"Job is canceled during initialize.");
                    Trace.Error($"Caught exception: {ex}");

                    FinalizeGcp(jobContext, message, vmSpecs);

                    return await CompleteJobAsync(server, jobContext, message, TaskResult.Canceled);
                }
                catch (Exception ex)
                {
                    // set the job to failed.
                    // don't log error issue to job ExecutionContext, since server owns the job level issue
                    Trace.Error($"Job initialize failed.");
                    Trace.Error($"Caught exception from {nameof(jobExtension.InitializeJob)}: {ex}");

                    FinalizeGcp(jobContext, message, vmSpecs);

                    return await CompleteJobAsync(server, jobContext, message, TaskResult.Failed);
                }

                // trace out all steps
                Trace.Info($"Total job steps: {jobSteps.Count}.");
                Trace.Verbose($"Job steps: '{string.Join(", ", jobSteps.Select(x => x.DisplayName))}'");
                HostContext.WritePerfCounter($"WorkerJobInitialized_{message.RequestId.ToString()}");

                // Run all job steps
                Trace.Info("Run all job steps.");
                var stepsRunner = HostContext.GetService<IStepsRunner>();
                try
                {
                    foreach (var step in jobSteps)
                    {
                        jobContext.JobSteps.Enqueue(step);
                    }

                    await stepsRunner.RunAsync(jobContext);
                }
                catch (Exception ex)
                {
                    // StepRunner should never throw exception out.
                    // End up here mean there is a bug in StepRunner
                    // Log the error and fail the job.
                    Trace.Error($"Caught exception from job steps {nameof(StepsRunner)}: {ex}");
                    jobContext.Error(ex);
                    return await CompleteJobAsync(server, jobContext, message, TaskResult.Failed);
                }
                finally
                {
                    Trace.Info("Finalize job.");
                    GCPCoordinator.RunProcess(
                            fileName: "python3",
                            arguments: $"vm_command.py --mode check_dmesg -n {instanceNumber}",
                            workDirectory: virtDir,
                            outputDataReceivedFunc: (_, args) => { Trace.Info(args.Data ?? ""); jobContext.Warning(args.Data ?? ""); },
                            errorDataReceivedFunc: (_, args) => Trace.Error(args.Data ?? ""),
                            exceptionFunc: (e) => { Trace.Info("Exception when checking rsyslog!"); Trace.Info(e.Message); },
                            trace: Trace,
                            exceptionReturnCode: 255);
                    FinalizeGcp(jobContext, message, vmSpecs);

                    jobExtension.FinalizeJob(jobContext, message, jobStartTimeUtc);
                }

                Trace.Info($"Job result after all job steps finish: {jobContext.Result ?? TaskResult.Succeeded}");

                Trace.Info("Completing the job execution context.");
                return await CompleteJobAsync(server, jobContext, message);
            }
            finally
            {
                Trace.Info("Entering finally block.");
                if (runnerShutdownRegistration != null)
                {
                    runnerShutdownRegistration.Value.Dispose();
                    runnerShutdownRegistration = null;
                }

                await ShutdownQueue(throwOnFailure: false);
            }
        }

        private async Task<TaskResult> CompleteJobAsync(IRunnerService server, IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message, TaskResult? taskResult = null)
        {
            if (server is IRunServer runServer)
            {
                return await CompleteJobAsync(runServer, jobContext, message, taskResult);
            }
            else if (server is IJobServer jobServer)
            {
                return await CompleteJobAsync(jobServer, jobContext, message, taskResult);
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private async Task<TaskResult> CompleteJobAsync(IRunServer runServer, IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message, TaskResult? taskResult = null)
        {
            jobContext.Debug($"Finishing: {message.JobDisplayName}");
            TaskResult result = jobContext.Complete(taskResult);

            // Make sure to clean temp after file upload since they may be pending fileupload still use the TEMP dir.
            _tempDirectoryManager?.CleanupTempDirectory();

            // Load any upgrade telemetry
            //LoadFromTelemetryFile(jobContext.Global.JobTelemetry);

            //// Make sure we don't submit secrets as telemetry
            //MaskTelemetrySecrets(jobContext.Global.JobTelemetry);

            Trace.Info($"Raising job completed against run service");
            var completeJobRetryLimit = 5;
            var exceptions = new List<Exception>();
            while (completeJobRetryLimit-- > 0)
            {
                try
                {
                    await runServer.CompleteJobAsync(message.Plan.PlanId, message.JobId, result, jobContext.JobOutputs, jobContext.Global.StepsResult, default);
                    return result;
                }
                catch (Exception ex)
                {
                    Trace.Error($"Catch exception while attempting to complete job {message.JobId}, job request {message.RequestId}.");
                    Trace.Error(ex);
                    exceptions.Add(ex);
                }

                // delay 5 seconds before next retry.
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            // rethrow exceptions from all attempts.
            throw new AggregateException(exceptions);
        }

        private bool DecodeBase64OrAddIssue(KeyValuePair<String, String> envPair, IExecutionContext jobContext)
        {
            try
            {
                Encoding.UTF8.GetString(Convert.FromBase64String(envPair.Value)); 
                return true;
            }
            catch (FormatException e)
            {
                jobContext.AddIssue(
                        new Issue() { 
                            Type = IssueType.Warning, 
                            Message = $"Some features will not be enabled as the value of {envPair.Key} is not a valid Base64 string." 
                            }
                        );

                Trace.Error($"{envPair.Key} is invalid: {e.Message}");

                return false;
            }
        }

        private bool FinalizeGcp(IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message, dynamic vmSpecs)
        {
            IExecutionContext vmCtx = jobContext.CreateChild(Guid.NewGuid(), "Teardown VM", "VM_teardown", null, null, ActionRunStage.Main);
            vmCtx.Start();

            // Variables inferred from environment.
            var instanceNumber = Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable);
            var sshIp = Environment.GetEnvironmentVariable(Constants.RunnerIPVariable);

            // Variables inferred from modified JSON message.
            var virtDir = message.Variables["system.qemuDir"].Value;
            // ssh master connection needs to be closed before trying to unmount
            // sshfs, otherwise, umount will fail
            var sshArguments = new List<string>(Constants.CommonSshArgs);
            sshArguments.Add($"-O exit scalerunner@{sshIp}");

            GCPCoordinator.RunProcess(
                    fileName: "ssh",
                    arguments: string.Join(" ", sshArguments.ToArray()),
                    workDirectory: virtDir,
                    outputDataReceivedFunc: (_, args) => Trace.Info(args.Data ?? ""),
                    errorDataReceivedFunc: (_, args) => Trace.Error(args.Data ?? ""),
                    exceptionFunc: (e) => { Trace.Info("Exception when closing master ssh connection!"); Trace.Info(e.Message); },
                    trace: Trace,
                    exceptionReturnCode: 255);

            Trace.Info($"Destroying {Constants.RunnerIPVariable}");

            GCPCoordinator.RunProcess(
                    fileName: "python3",
                    arguments: $"vm_command.py --mode print_ascii_graph -n {instanceNumber}",
                    workDirectory: virtDir,
                    outputDataReceivedFunc: (_, args) => { vmCtx.Output(args.Data ?? ""); Trace.Info(args.Data ?? ""); },
                    errorDataReceivedFunc: (_, args) => Trace.Error(args.Data ?? ""),
                    exceptionFunc: (e) => { Trace.Info("Exception when trying to fetch ASCII graphs!"); Trace.Info(e.Message); },
                    trace: Trace,
                    exceptionReturnCode: 255);

            GCPCoordinator.RunProcess(
                    fileName: "python3",
                    arguments: $"vm_command.py --mode delete_vm -n {instanceNumber}",
                    workDirectory: virtDir,
                    outputDataReceivedFunc: (_, args) => Trace.Info(args.Data ?? ""),
                    errorDataReceivedFunc: (_, args) => Trace.Error(args.Data ?? ""),
                    exceptionFunc: (e) => { Trace.Info("Exception when trying to delete VM!"); Trace.Info(e.Message); },
                    trace: Trace,
                    exceptionReturnCode: 255);

            vmCtx.Complete();
            return true;
        }

        private int StartGcpMachine(String spawnMachineArgs, String virtDir, StringBuilder output_string, IExecutionContext vmCtx, IExecutionContext jobContext) {
            Trace.Info($"Starting VM");
            var acm = HostContext.CreateService<IActionCommandManager>();
            return GCPCoordinator.RunProcess(
                    fileName: "python3",
                    arguments: spawnMachineArgs,
                    workDirectory: virtDir,
                    outputDataReceivedFunc: (_, args) => 
                        {
                            output_string.Append(args.Data + "\n" ?? "");
                            if (!acm.TryProcessCommand(vmCtx, args.Data ?? "", null)) {
                                vmCtx.Output(args.Data ?? "");
                            }
                            Trace.Info(args.Data ?? "");
                        },
                    errorDataReceivedFunc: (_, args) => Trace.Error(args.Data ?? ""),
                    exceptionFunc: (e) => { Trace.Info("Exception when trying to start VM!"); Trace.Info(e.Message); },
                    trace: Trace,
                    exceptionReturnCode: 255);
        }

        private bool SetRunnerIP(String output) {
            // Interpret special lines coming from the VM starter script.
            using (var reader = new StringReader(output))
            {
                for (string line = reader.ReadLine(); line != null; line = reader.ReadLine())
                {
                    if (line.Contains("export")) {
                        line = line.Remove(0, "export ".Length);
                        var export_val = line.Split("=");
                        if (export_val.Length == 2)
                        {
                            Trace.Info($"Setting {export_val[0]} to {export_val[1]}");
                            Environment.SetEnvironmentVariable(export_val[0], export_val[1]);
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private void RestartGcpMachine(int exitCode, IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message, dynamic vmSpecs, ref IExecutionContext vmCtx) {
                var vmNonZeroExitCode = $"VM starter exited with non-zero exit code: {exitCode}";
                Trace.Error(vmNonZeroExitCode);
                vmCtx.Complete(); // Complete Set up VM step

                FinalizeGcp(jobContext, message, vmSpecs); // Start Teardown VM step

                Trace.Info("Finished finalizing GCP after VM starter failure.");
                vmCtx = jobContext.CreateChild(Guid.NewGuid(), "Set up VM", "VM_Init", null, null, ActionRunStage.Main); // Create new Start up VM step
                vmCtx.Start();
        }

        private bool JobPassesSecurityRestrictions(IExecutionContext jobContext)
        {
            var gitHubContext = jobContext.ExpressionValues["github"] as GitHubContext;

            try {
              if (gitHubContext.IsPullRequest())
              {
                  return OkayToRunPullRequest(gitHubContext);
              }

              return true;
            }
            catch (Exception ex)
            {
                Trace.Error("Caught exception in JobPassesSecurityRestrictions");
                Trace.Error("As a safety precaution we are not allowing this job to run");
                Trace.Error(ex);
                return false;
            }
        }

        private bool IsPullRequestFromFork(GitHubContext gitHubContext)
        {
            if(!gitHubContext.IsPullRequest())
                return false;

            var githubEvent = gitHubContext["event"] as DictionaryContextData;
            var prData = githubEvent["pull_request"] as DictionaryContextData;
            var prHead = prData["head"] as DictionaryContextData;
            var prRepo = prHead["repo"] as DictionaryContextData;
            var prFork = prRepo.TryGetValue("fork", out var value) ? value as BooleanContextData : null;
            Trace.Info($"IsPullRequestFromFork: {prFork}");
            return prFork;
        }

        private bool OkayToRunPullRequest(GitHubContext gitHubContext)
        {
            var configStore = HostContext.GetService<IConfigurationStore>();
            var settings = configStore.GetSettings();
            var prSecuritySettings = settings.PullRequestSecuritySettings;

            if (prSecuritySettings is null) {
                Trace.Info("No pullRequestSecurity defined in settings, allowing this build");
                return true;
            }

            var githubEvent = gitHubContext["event"] as DictionaryContextData;
            var prData = githubEvent["pull_request"] as DictionaryContextData;

            var authorAssociation = prData.TryGetValue("author_association", out var value)
              ? value as StringContextData : null;


            // TODO: Allow COLLABORATOR, MEMBER too -- possibly by a config setting
            if (authorAssociation == "OWNER")
            {
                Trace.Info("PR is from the repo owner, always allowed");
                return true;
            }
            else if (prSecuritySettings.AllowContributors && authorAssociation == "COLLABORATOR") {
                Trace.Info("PR is from the repo collaborator, allowing");
                return true;
            }

            var prHead = prData["head"] as DictionaryContextData;
            var prUser = prHead["user"] as DictionaryContextData;
            var prUserLogin = prUser["login"] as StringContextData;

            Trace.Info($"GitHub PR author is {prUserLogin as StringContextData}");

            if (prUserLogin == null)
            {
                Trace.Info("Unable to get PR author, not allowing PR to run");
                return false;
            }

            if (prSecuritySettings.AllowedAuthors.Contains(prUserLogin))
            {
                Trace.Info("Author in PR allowed list");
                return true;
            }
            else
            {
                Trace.Info($"Not running job as author ({prUserLogin}) is not in {{{string.Join(", ", prSecuritySettings.AllowedAuthors)}}}");

                return false;
            }
        }

        private async Task<TaskResult> CompleteJobAsync(IJobServer jobServer, IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message, TaskResult? taskResult = null)
        {
            jobContext.Debug($"Finishing: {message.JobDisplayName}");
            TaskResult result = jobContext.Complete(taskResult);

            //var totalTimeMs = (long) (DateTime.UtcNow - jobStartTime).TotalMilliseconds;
            var totalTimeMs = 1000; // TODO: fix this later
            Trace.Info($"Total time of current job: {totalTimeMs} ms");
            await _besServerClient.DeleteTarget(jobContext.ExpressionValues["github"] as GitHubContext, (int) result, totalTimeMs);

            try
            {
                await ShutdownQueue(throwOnFailure: true);
            }
            catch (Exception ex)
            {
                Trace.Error($"Caught exception from {nameof(JobServerQueue)}.{nameof(_jobServerQueue.ShutdownAsync)}");
                Trace.Error("This indicate a failure during publish output variables. Fail the job to prevent unexpected job outputs.");
                Trace.Error(ex);
                result = TaskResultUtil.MergeTaskResults(result, TaskResult.Failed);
            }

            // Clean TEMP after finish process jobserverqueue, since there might be a pending fileupload still use the TEMP dir.
            _tempDirectoryManager?.CleanupTempDirectory();

            if (!jobContext.Global.Features.HasFlag(PlanFeatures.JobCompletedPlanEvent))
            {
                Trace.Info($"Skip raise job completed event call from worker because Plan version is {message.Plan.Version}");
                return result;
            }

            Trace.Info("Raising job completed event.");
            var jobCompletedEvent = new JobCompletedEvent(message.RequestId, message.JobId, result, jobContext.JobOutputs, jobContext.ActionsEnvironment);

            var completeJobRetryLimit = 5;
            var exceptions = new List<Exception>();
            while (completeJobRetryLimit-- > 0)
            {
                try
                {
                    await jobServer.RaisePlanEventAsync(message.Plan.ScopeIdentifier, message.Plan.PlanType, message.Plan.PlanId, jobCompletedEvent, default(CancellationToken));
                    return result;
                }
                catch (TaskOrchestrationPlanNotFoundException ex)
                {
                    Trace.Error($"TaskOrchestrationPlanNotFoundException received, while attempting to raise JobCompletedEvent for job {message.JobId}.");
                    Trace.Error(ex);
                    return TaskResult.Failed;
                }
                catch (TaskOrchestrationPlanSecurityException ex)
                {
                    Trace.Error($"TaskOrchestrationPlanSecurityException received, while attempting to raise JobCompletedEvent for job {message.JobId}.");
                    Trace.Error(ex);
                    return TaskResult.Failed;
                }
                catch (TaskOrchestrationPlanTerminatedException ex)
                {
                    Trace.Error($"TaskOrchestrationPlanTerminatedException received, while attempting to raise JobCompletedEvent for job {message.JobId}.");
                    Trace.Error(ex);
                    return TaskResult.Failed;
                }
                catch (Exception ex)
                {
                    Trace.Error($"Catch exception while attempting to raise JobCompletedEvent for job {message.JobId}, job request {message.RequestId}.");
                    Trace.Error(ex);
                    exceptions.Add(ex);
                }

                // delay 5 seconds before next retry.
                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            // rethrow exceptions from all attempts.
            throw new AggregateException(exceptions);
        }

        private async Task ShutdownQueue(bool throwOnFailure)
        {
            if (_jobServerQueue != null)
            {
                try
                {
                    Trace.Info("Shutting down the job server queue.");
                    await _jobServerQueue.ShutdownAsync();
                }
                catch (Exception ex) when (!throwOnFailure)
                {
                    Trace.Error($"Caught exception from {nameof(JobServerQueue)}.{nameof(_jobServerQueue.ShutdownAsync)}");
                    Trace.Error(ex);
                }
                finally
                {
                    _jobServerQueue = null; // Prevent multiple attempts.
                }
            }
        }
    }
}
