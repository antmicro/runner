using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading.Channels;
using GitHub.Runner.Common;
using GitHub.Runner.Common.Util;
using GitHub.Runner.Sdk;
using GitHub.Runner.GCP;
using GitHub.DistributedTask.WebApi;
using GitHub.DistributedTask.Pipelines.ContextData;
using Pipelines = GitHub.DistributedTask.Pipelines;
using System;
using System.Linq;

namespace GitHub.Runner.Worker.Handlers
{
    [ServiceLocator(Default = typeof(NodeScriptActionHandler))]
    public interface INodeScriptActionHandler : IHandler
    {
        NodeJSActionExecutionData Data { get; set; }
    }

    public sealed class NodeScriptActionHandler : Handler, INodeScriptActionHandler
    {
        public NodeJSActionExecutionData Data { get; set; }

        public async Task RunAsync(ActionRunStage stage)
        {
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(Data, nameof(Data));
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(Inputs, nameof(Inputs));
            ArgUtil.Directory(ActionDirectory, nameof(ActionDirectory));

            // Update the env dictionary.
            AddInputsToEnvironment();
            AddPrependPathToEnvironment();

            // expose context to environment
            foreach (var context in ExecutionContext.ExpressionValues)
            {
                if (context.Value is IEnvironmentContextData runtimeContext && runtimeContext != null)
                {
                    foreach (var env in runtimeContext.GetRuntimeEnvironmentVariables())
                    {
                        Environment[env.Key] = env.Value;
                    }
                }
            }

            // Add Actions Runtime server info
            var systemConnection = ExecutionContext.Global.Endpoints.Single(x => string.Equals(x.Name, WellKnownServiceEndpointNames.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
            Environment["ACTIONS_RUNTIME_URL"] = systemConnection.Url.AbsoluteUri;
            Environment["ACTIONS_RUNTIME_TOKEN"] = systemConnection.Authorization.Parameters[EndpointAuthorizationParameters.AccessToken];
            if (systemConnection.Data.TryGetValue("CacheServerUrl", out var cacheUrl) && !string.IsNullOrEmpty(cacheUrl))
            {
                Environment["ACTIONS_CACHE_URL"] = cacheUrl;
            }

            // Resolve the target script.
            string target = null;
            if (stage == ActionRunStage.Main)
            {
                target = Data.Script;
            }
            else if (stage == ActionRunStage.Pre)
            {
                target = Data.Pre;
            }
            else if (stage == ActionRunStage.Post)
            {
                target = Data.Post;
            }

            ArgUtil.NotNullOrEmpty(target, nameof(target));
            target = Path.Combine(ActionDirectory, target);
            ArgUtil.File(target, nameof(target));

            // Resolve the working directory.
            string workingDirectory = ExecutionContext.GetGitHubContext("workspace");
            if (string.IsNullOrEmpty(workingDirectory))
            {
                workingDirectory = HostContext.GetDirectory(WellKnownDirectory.Work);
            }

            Trace.Info($"workspace: {workingDirectory}");

            var actionName = ActionDirectory.Split("_actions/")[1];

            Trace.Info($"Stage: {stage}, target: {target}, action dir: {ActionDirectory}, action name: {actionName}");

            var jobName = System.Environment.GetEnvironmentVariable("GITHUB_JOB_FULL");
            Trace.Info($"jobName: {jobName}");

            // Get GitHubContext and use it to retrieve some variables that were inserted into it.
            // This is used as a form of hacky message passing.
            var githubContext = ExecutionContext.ExpressionValues["github"] as GitHubContext;
            var sshIp = System.Environment.GetEnvironmentVariable(Constants.RunnerIPVariable);

            if (actionName == "actions/upload-artifact/v2")
            {
                var instanceNumber = System.Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable);
                var virtDir = Path.Combine(new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Root)).Parent.FullName, "virt");

                var tempDir = HostContext.GetDirectory(WellKnownDirectory.Temp);
                var plotRemotePath = "/mnt/plot.svg";

                var sargraphSshArguments = new List<string>(Constants.CommonSshArgs);
                sargraphSshArguments.Add($"scalerunner@{sshIp} -t bash -c 'sudo sargraph chart stop && sudo chmod 777 {plotRemotePath}'");
                var sargraphStopExitCode = GCPCoordinator.RunProcess(
                        fileName: "ssh",
                        arguments: string.Join(" ", sargraphSshArguments.ToArray()),
                        workDirectory: virtDir,
                        outputDataReceivedFunc: (_, args) => Trace.Info(args.Data ?? ""),
                        errorDataReceivedFunc: (_, args) => Trace.Info(args.Data ?? ""),
                        exceptionFunc: (e) => { Trace.Info("Exception when stopping sargraph!"); Trace.Info(e.Message); },
                        trace: Trace,
                        exceptionReturnCode: 255);

                GCPCoordinator.RunProcess(
                        fileName: "bash",
                        arguments: $"symlink_resolve.sh {sshIp}",
                        workDirectory: virtDir,
                        outputDataReceivedFunc: (_, args) => Trace.Info(args.Data ?? ""),
                        errorDataReceivedFunc: (_, args) => Trace.Info(args.Data ?? ""),
                        exceptionFunc: (e) => { Trace.Info("Exception when resolving symlink!"); Trace.Info(e.Message); },
                        trace: Trace,
                        exceptionReturnCode: 255);

                var runnerFileCommands = Path.Combine(tempDir, "_runner_file_commands");

                // Obtain the last component from working directory.
                // Consider the following directory:
                // /home/$USER/github-actions-runner/_layout/_work_0/$REPO/$REPO
                // 
                // $REPO/$REPO is that last component.
                var workspaceLastComponent = githubContext["container_workspace"];

                var plotCpArgs = new List<string>(Constants.CommonSshArgs);
                plotCpArgs.Add($"scalerunner@{sshIp} sudo cp {plotRemotePath} /mnt/2/{workspaceLastComponent}/plot_{jobName}.svg");
                if (sargraphStopExitCode == 0) {
                    GCPCoordinator.RunProcess(
                        fileName: "ssh",
                        arguments: string.Join(" ", plotCpArgs),
                        workDirectory: virtDir,
                        outputDataReceivedFunc: (_, args) => Trace.Info(args.Data ?? ""),
                        errorDataReceivedFunc: (_, args) => Trace.Info(args.Data ?? ""),
                        exceptionFunc: (e) => { Trace.Info("Exception when resolving symlink!"); Trace.Info(e.Message); },
                        trace: Trace,
                        exceptionReturnCode: 255);
                }
            }

            string file = "node";

            // Format the arguments passed to node.
            // 1) Wrap the script file path in double quotes.
            // 2) Escape double quotes within the script file path. Double-quote is a valid
            // file name character on Linux.
            string arguments_node = GCPRunner.TranslateToGCPRunnerPath(StringUtil.Format(@"""{0}""", target.Replace(@"""", @"\""")));

            var fileName = "/usr/bin/ssh";
            var sshArguments = new List<string>(Constants.CommonSshArgs);
            sshArguments.Add($"scalerunner@{sshIp} sudo singularity exec -e instance://node bash");
            var arguments = string.Join(" ", sshArguments.ToArray());

#if OS_WINDOWS
            // It appears that node.exe outputs UTF8 when not in TTY mode.
            Encoding outputEncoding = Encoding.UTF8;
#else
            // Let .NET choose the default.
            Encoding outputEncoding = null;
#endif
            string prepend = string.Join(Path.PathSeparator.ToString(), ExecutionContext.Global.PrependPath.Reverse<string>());
            Trace.Info($"Prepend: {prepend}");
            var workspaceDir = githubContext["workspace"] as StringContextData;
            workspaceDir = GCPRunner.TranslateToGCPRunnerPath(workingDirectory);
            Trace.Info($"Workspace from githubContext is {workspaceDir}");

            using (var stdoutManager = new OutputManager(ExecutionContext, ActionCommandManager))
            using (var stderrManager = new OutputManager(ExecutionContext, ActionCommandManager))
            {
                StepHost.OutputDataReceived += stdoutManager.OnDataReceived;
                StepHost.ErrorDataReceived += stderrManager.OnDataReceived;
                var input = Channel.CreateBounded<string>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
                // Github is using not POSIX-compliant environment variable names with '-'
                // to overcome this issue, we need to use env to set-up this variables.
                // It doesn't export variables, so execution of the node needs to be in the same process, e.g.:
                // env TEST-VAR=abc TEST-VAR2=def node index.js
                string exportStanzas = $"cd {workspaceDir} && env";

                var pathSuffix = "${PATH:+:${PATH}}";

                foreach (var e in Environment)
                {
                    var exportStr = $" {e.Key}=\"{GCPRunner.TranslateToGCPRunnerPath(e.Value.Replace("\"", "\\\""))}\"";
                    Trace.Info(exportStr);
                    exportStanzas += exportStr;
                }

                exportStanzas += $" PATH={prepend}{pathSuffix}";

                var initCmd = $"### START ###\n" + exportStanzas + " " + file + " " + arguments_node + $"\n### END ###";

                Trace.Info(initCmd);

                input.Writer.TryWrite(initCmd);
                StepHost.StandardInChannel = input;
                // Execute the process. Exit code 0 should always be returned.
                // A non-zero exit code indicates infrastructural failure.
                // Task failure should be communicated over STDOUT using ## commands.
                int exitCode = await StepHost.ExecuteAsync(workingDirectory: StepHost.ResolvePathForStepHost(workingDirectory),
                                                fileName: fileName,
                                                arguments: arguments,
                                                environment: Environment,
                                                requireExitCodeZero: false,
                                                outputEncoding: outputEncoding,
                                                killProcessOnCancel: false,
                                                inheritConsoleHandler: !ExecutionContext.Global.Variables.Retain_Default_Encoding,
                                                cancellationToken: ExecutionContext.CancellationToken);

                if (exitCode != 0) {
                    ExecutionContext.Error($"Process completed with exit code {exitCode}.");
                    ExecutionContext.Result = TaskResult.Failed;
                }

                StepHost.StandardInChannel = null;
            }
        }
    }
}
