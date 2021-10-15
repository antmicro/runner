using GitHub.DistributedTask.WebApi;
using Pipelines = GitHub.DistributedTask.Pipelines;
using GitHub.Runner.Common.Util;
using GitHub.Services.Common;
using GitHub.Services.WebApi;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;
using GitHub.DistributedTask.Pipelines.ContextData;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GitHub.Runner.Worker
{
    [ServiceLocator(Default = typeof(JobRunner))]
    public interface IJobRunner : IRunnerService
    {
        Task<TaskResult> RunAsync(Pipelines.AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken);
    }

    public sealed class JobRunner : RunnerService, IJobRunner
    {
        private IJobServerQueue _jobServerQueue;
        private ITempDirectoryManager _tempDirectoryManager;

        public async Task<TaskResult> RunAsync(Pipelines.AgentJobRequestMessage message, CancellationToken jobRequestCancellationToken)
        {
            // Validate parameters.
            Trace.Entering();
            ArgUtil.NotNull(message, nameof(message));
            ArgUtil.NotNull(message.Resources, nameof(message.Resources));
            ArgUtil.NotNull(message.Variables, nameof(message.Variables));
            ArgUtil.NotNull(message.Steps, nameof(message.Steps));
            Trace.Info("Job ID {0}", message.JobId);

            DateTime jobStartTimeUtc = DateTime.UtcNow;

            ServiceEndpoint systemConnection = message.Resources.Endpoints.Single(x => string.Equals(x.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));

            // Spawn Google VM
            var spawnMachineProc = new Process();
            var sshfsProc = new Process();
            var instanceNumber = Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable);
            var rootDir = new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Root)).Parent.FullName;
            var virtDir = Path.Combine(rootDir, "virt");
            var ghJson = message.ContextData["github"].ToJToken();
            string virtIp = $"{Environment.MachineName}-auto-spawned{instanceNumber}";

            Trace.Info($"Runner instance: {instanceNumber}");

            Trace.Info($"QEMU tools directory: {virtDir}");

            Trace.Info($"Job container: {message.JobContainer}");

            var repoFullName = $"{message.ContextData["github"].ToJToken()["repository"]}";
            var repoName = repoFullName.Substring(repoFullName.LastIndexOf('/') + 1);
            Trace.Info($"Full repo name: {repoFullName}");
            Trace.Info($"Repo name: {repoName}");

            var PipelineDirectory = repoName.ToString(CultureInfo.InvariantCulture);
            var WorkspaceDirectory = Path.Combine(PipelineDirectory, repoName);

            Trace.Info($"PipelineDirectory: {PipelineDirectory}");
            Trace.Info($"WorkspaceDirectory: {WorkspaceDirectory}");

            message.Variables["system.qemuDir"] = virtDir;
            message.Variables["system.qemuIp"] = virtIp;
            message.Variables["system.containerWorkspace"] = WorkspaceDirectory;

            string messageSerialized = JsonConvert.SerializeObject(message);
            JObject messageJson = JObject.Parse(messageSerialized);

            var externalDisk = String.Empty;

            // We're parsing the JSON that looks like the example below:
            //
            // ```
            // (...)
            // "EnvironmentVariables": [
            // {
            //     "type": 2,
            //         "file": 1,
            //         "line": 18,
            //         "col": 7,
            //         "map": [
            //         {
            //             "Key": {
            //                 "type": 0,
            //                 "file": 1,
            //                 "line": 18,
            //                 "col": 7,
            //                 "lit": "GHA_EXTERNAL_DISK"
            //             },
            //             "Value": {
            //                 "type": 0,
            //                 "file": 1,
            //                 "line": 18,
            //                 "col": 20,
            //                 "lit": "auxdisk"
            //             }
            //         },
            // (...)
            // ```
            try
            {
                foreach (var envList in messageJson["EnvironmentVariables"])
                {
                    foreach (var envEntry in envList["map"])
                    {
                        if ((string)envEntry["Key"]["lit"] == "GHA_EXTERNAL_DISK")
                        {
                            externalDisk = (string)envEntry["Value"]["lit"];
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Trace.Warning($"Couldn't extract external disk variable: {e}");
            }
            
            Trace.Info($"External disk: {externalDisk}");

            Trace.Info($"VIRT IP: {virtIp}");

            dynamic vmSpecs = JObject.Parse(File.ReadAllText(Path.Combine(rootDir, ".vm_specs.json")));

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
            HostContext.WritePerfCounter($"WorkerJobServerQueueStarted_{message.RequestId.ToString()}");

            IExecutionContext jobContext = null;
            CancellationTokenRegistration? runnerShutdownRegistration = null;
            try
            {
                // Create the job execution context.
                jobContext = HostContext.CreateService<IExecutionContext>();
                jobContext.InitializeJob(message, jobRequestCancellationToken);
                Trace.Info("Starting the job execution context.");
                jobContext.Start();
                var githubContext = jobContext.ExpressionValues["github"] as GitHubContext;

                var templateEval = jobContext.ToPipelineTemplateEvaluator();
                var container = templateEval.EvaluateJobContainer(message.JobContainer, jobContext.ExpressionValues, jobContext.ExpressionFunctions);

                if (!JobPassesSecurityRestrictions(jobContext))
                {
                    jobContext.Error("Running job on this worker disallowed by security policy");
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }
                
                IExecutionContext vmCtx = jobContext.CreateChild(Guid.NewGuid(), "Set up VM", "VM_Init", null, null);
                vmCtx.Start();

                if (IsGcpMachineRunning(virtIp, vmSpecs))
                {
                    vmCtx.Output("Removing stale worker...");
                    FinalizeGcp(jobContext, message, vmSpecs);
                    vmCtx.Output("Stale worker removed.");
                }

                Trace.Info($"Container: ${container.Image}");

                spawnMachineProc.StartInfo.FileName = WhichUtil.Which("python3", trace: Trace);
                spawnMachineProc.StartInfo.Arguments = $"create_preemptible_vm.py -n {instanceNumber} -s {container.Image}";
                spawnMachineProc.StartInfo.WorkingDirectory = virtDir;
                spawnMachineProc.StartInfo.UseShellExecute = false;
                spawnMachineProc.StartInfo.RedirectStandardError = true;
                spawnMachineProc.StartInfo.RedirectStandardOutput = true;

                spawnMachineProc.OutputDataReceived += (_, args) => 
                {
                    vmCtx.Output(args.Data ?? "");
                    Trace.Info(args.Data ?? "");
                };
                // Log stderr to local logfile only to avoid potential leaks.
                spawnMachineProc.ErrorDataReceived += (_, args) => Trace.Error(args.Data ?? "");

                sshfsProc.StartInfo.FileName = WhichUtil.Which("bash", trace: Trace);
                sshfsProc.StartInfo.Arguments = $"sshfs.sh mount {instanceNumber}";
                sshfsProc.StartInfo.WorkingDirectory = virtDir;
                sshfsProc.StartInfo.UseShellExecute = false;
                sshfsProc.StartInfo.RedirectStandardError = true;
                sshfsProc.StartInfo.RedirectStandardOutput = true;

                sshfsProc.OutputDataReceived += (_, args) => Trace.Info(args.Data ?? "");
                sshfsProc.ErrorDataReceived += (_, args) => Trace.Error(args.Data ?? "");

                spawnMachineProc.Start();
                spawnMachineProc.BeginOutputReadLine();
                spawnMachineProc.BeginErrorReadLine();

                Trace.Info($"Starting VM with start script PID {spawnMachineProc.Id}");

                spawnMachineProc.WaitForExit();

                if (spawnMachineProc.ExitCode != 0)
                {
                    var vmNonZeroExitCode = $"VM starter exited with non-zero exit code: {spawnMachineProc.ExitCode}";

                    vmCtx.Error(vmNonZeroExitCode);
                    vmCtx.Result = TaskResult.Failed;

                    Trace.Error(vmNonZeroExitCode);
                    jobContext.Error(vmNonZeroExitCode);

                    FinalizeGcp(jobContext, message, vmSpecs);

                    Trace.Info("Finished finalizing GCP after VM starter failure.");

                    vmCtx.Complete();

                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }

                Trace.Info($"Mounting {WorkspaceDirectory} via sshfs...");
                vmCtx.Output("Mounting worker filesystem...");

                sshfsProc.Start();
                sshfsProc.BeginOutputReadLine();
                sshfsProc.BeginErrorReadLine();
                sshfsProc.WaitForExit(30000);
                sshfsProc.WaitForExit();

                if (sshfsProc.ExitCode != 0)
                {
                    Trace.Error($"sshfs started exited with {sshfsProc.ExitCode}");
                    jobContext.Error($"sshfs: exit code {sshfsProc.ExitCode}");

                    vmCtx.Error("Mounting worker filesystem failed!");
                    vmCtx.Result = TaskResult.Failed;

                    FinalizeGcp(jobContext, message, vmSpecs);

                    vmCtx.Complete();

                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }

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
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
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

                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Canceled);
                }
                catch (Exception ex)
                {
                    // set the job to failed.
                    // don't log error issue to job ExecutionContext, since server owns the job level issue
                    Trace.Error($"Job initialize failed.");
                    Trace.Error($"Caught exception from {nameof(jobExtension.InitializeJob)}: {ex}");

                    FinalizeGcp(jobContext, message, vmSpecs);

                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
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
                    return await CompleteJobAsync(jobServer, jobContext, message, TaskResult.Failed);
                }
                finally
                {
                    Trace.Info("Finalize job.");

                    FinalizeGcp(jobContext, message, vmSpecs);

                    jobExtension.FinalizeJob(jobContext, message, jobStartTimeUtc);
                }

                Trace.Info($"Job result after all job steps finish: {jobContext.Result ?? TaskResult.Succeeded}");

                Trace.Info("Completing the job execution context.");
                return await CompleteJobAsync(jobServer, jobContext, message);
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

        private bool IsGcpMachineRunning(string hostname, dynamic vmSpecs)
        {
            Trace.Entering();

            var zone = vmSpecs.gcp.zone;
            var gcloud = new Process();
            gcloud.StartInfo.FileName = WhichUtil.Which("gcloud", trace: Trace);
            gcloud.StartInfo.Arguments = $"beta compute instances describe {hostname} --zone={zone} --quiet";
            gcloud.StartInfo.UseShellExecute = false;
            gcloud.StartInfo.RedirectStandardError = true;
            gcloud.StartInfo.RedirectStandardOutput = true;

            gcloud.OutputDataReceived += (_, args) => Trace.Info(args.Data ?? "");
            gcloud.ErrorDataReceived += (_, args) => Trace.Error(args.Data ?? "");

            gcloud.Start();
            gcloud.WaitForExit();

            return gcloud.ExitCode == 0;
        }

        private bool FinalizeGcp(IExecutionContext jobContext, Pipelines.AgentJobRequestMessage message, dynamic vmSpecs)
        {
            var instanceNumber = Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable);
            var virtIp = message.Variables["system.qemuIp"].Value;
            var virtDir = message.Variables["system.qemuDir"].Value;
            var WorkspaceDirectory = message.Variables["system.containerWorkspace"].Value;
            var umountProc = new Process();

            umountProc.StartInfo.FileName = WhichUtil.Which("bash", trace: Trace);
            umountProc.StartInfo.Arguments = $"sshfs.sh umount {instanceNumber}";
            umountProc.StartInfo.WorkingDirectory = virtDir;
            umountProc.StartInfo.UseShellExecute = false;
            umountProc.StartInfo.RedirectStandardError = true;
            umountProc.StartInfo.RedirectStandardOutput = true;

            umountProc.OutputDataReceived += (_, args) => Trace.Info(args.Data ?? "");
            umountProc.ErrorDataReceived += (_, args) =>
            {
                Trace.Error(args.Data ?? "");
                jobContext.Error(args.Data ?? "");
            };

            var gZone = vmSpecs.gcp.zone;
            var gcloudDelProc = new Process();
            gcloudDelProc.StartInfo.FileName = WhichUtil.Which("gcloud", trace: Trace);
            gcloudDelProc.StartInfo.Arguments = $"compute instances delete --delete-disks=boot --zone={gZone} {virtIp}";
            gcloudDelProc.StartInfo.WorkingDirectory = virtDir;
            gcloudDelProc.StartInfo.UseShellExecute = false;
            gcloudDelProc.StartInfo.RedirectStandardError = true;
            gcloudDelProc.StartInfo.RedirectStandardOutput = true;

            gcloudDelProc.OutputDataReceived += (_, args) => Trace.Info(args.Data ?? "");
            gcloudDelProc.ErrorDataReceived += (_, args) => Trace.Error(args.Data ?? "");

            Trace.Info($"Unmouting sshfs from {WorkspaceDirectory}");
            umountProc.Start();
            umountProc.BeginOutputReadLine();
            umountProc.BeginErrorReadLine();
            umountProc.WaitForExit();

            Trace.Info($"Destroying {virtIp} from {gZone}");

            gcloudDelProc.Start();
            gcloudDelProc.BeginOutputReadLine();
            gcloudDelProc.BeginErrorReadLine();
            gcloudDelProc.WaitForExit();

            return true;
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
