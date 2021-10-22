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

            if (actionName == "actions/upload-artifact/v2")
            {
                var instanceNumber = System.Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable);
                var virtDir = Path.Combine(new DirectoryInfo(HostContext.GetDirectory(WellKnownDirectory.Root)).Parent.FullName, "virt");

                var tempDir = HostContext.GetDirectory(WellKnownDirectory.Temp);
                var plotRemotePath = "/mnt/plot.svg";

                var sargraphStop = new Process();
                sargraphStop.StartInfo.FileName = WhichUtil.Which("bash", trace: Trace);
                sargraphStop.StartInfo.Arguments = $"ssh.sh {instanceNumber} --sargraph-stop {plotRemotePath}";
                sargraphStop.StartInfo.WorkingDirectory = virtDir;
                sargraphStop.StartInfo.UseShellExecute = false;
                sargraphStop.StartInfo.RedirectStandardError = true;
                sargraphStop.StartInfo.RedirectStandardOutput = true;

                sargraphStop.OutputDataReceived += (_, args) => Trace.Info(args.Data);
                sargraphStop.ErrorDataReceived += (_, args) => Trace.Info(args.Data);

                sargraphStop.Start();
                sargraphStop.BeginOutputReadLine();
                sargraphStop.BeginErrorReadLine();

                var symFix = new Process();
                symFix.StartInfo.FileName = WhichUtil.Which("bash", trace: Trace);
                symFix.StartInfo.Arguments = $"symlink_resolve.sh {instanceNumber}";
                symFix.StartInfo.WorkingDirectory = virtDir;
                symFix.StartInfo.UseShellExecute = false;
                symFix.StartInfo.RedirectStandardError = true;
                symFix.StartInfo.RedirectStandardOutput = true;

                symFix.OutputDataReceived += (_, args) => Trace.Info(args.Data);
                symFix.ErrorDataReceived += (_, args) => Trace.Info(args.Data);

                symFix.Start();
                symFix.BeginOutputReadLine();
                symFix.BeginErrorReadLine();

                Trace.Info($"Starting {symFix.StartInfo.Arguments} with PID {symFix.Id}");

                var runnerFileCommands = Path.Combine(tempDir, "_runner_file_commands");

                var plotGet = new Process();
                var plotGetArgs = new List<string> {"-q",
                    "-o UserKnownHostsFile=/dev/null", 
                    "-o StrictHostKeyChecking=no",
                    "-i ~/.ssh/id_rsa",
                    $"scalerunner@{System.Environment.MachineName}-auto-spawned{instanceNumber}:{plotRemotePath}",
                    $"{runnerFileCommands}"
                };

                plotGet.StartInfo.FileName = WhichUtil.Which("scp", trace: Trace);
                plotGet.StartInfo.Arguments = string.Join(" ", plotGetArgs);
                plotGet.StartInfo.WorkingDirectory = virtDir;
                plotGet.StartInfo.UseShellExecute = false;
                plotGet.StartInfo.RedirectStandardError = true;
                plotGet.StartInfo.RedirectStandardOutput = true;

                plotGet.OutputDataReceived += (_, args) => Trace.Info(args.Data);
                plotGet.ErrorDataReceived += (_, args) => Trace.Info(args.Data);

                // Wait 3 minutes for processes to exit
                var procTimeout = 180000;
                while (!symFix.WaitForExit(procTimeout));
                while (!sargraphStop.WaitForExit(procTimeout));

                Trace.Info($"{symFix.StartInfo.Arguments} exit code: {symFix.ExitCode}");
                Trace.Info($"{sargraphStop.StartInfo.Arguments} exit code: {sargraphStop.ExitCode}");

                if (sargraphStop.ExitCode == 0)
                {
                    plotGet.Start();
                    plotGet.BeginOutputReadLine();
                    plotGet.BeginErrorReadLine();

                    plotGet.WaitForExit();
                    Trace.Info($"{plotGet.StartInfo.Arguments} exit code: {plotGet.ExitCode}");

                    File.Copy(
                            Path.Combine(runnerFileCommands, "plot.svg"),
                            Path.Combine(workingDirectory, $"plot_{jobName}.svg"));
                }
            }

            string file = "node";

            // Format the arguments passed to node.
            // 1) Wrap the script file path in double quotes.
            // 2) Escape double quotes within the script file path. Double-quote is a valid
            // file name character on Linux.
            string arguments_node = GCPRunner.TranslateToGCPRunnerPath(StringUtil.Format(@"""{0}""", target.Replace(@"""", @"\""")));
            var githubContext = ExecutionContext.ExpressionValues["github"] as GitHubContext;
            var sshIp = githubContext["qemu_ip"];

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

                var envCmdDir = "_runner_file_commands/";
                var remoteEnvDir = "/9p";
                var ghPath = $"{remoteEnvDir}/{Environment["GITHUB_PATH"].Split(envCmdDir)[1]}";
                var pathSuffix = "${PATH:+:${PATH}}";

                foreach (var e in Environment)
                {
                    var exportStr = $" {e.Key}=\"{GCPRunner.TranslateToGCPRunnerPath(e.Value.Replace("\"", "\\\""))}\"";
                    Trace.Info(exportStr);
                    exportStanzas += exportStr;
                }

                exportStanzas += $" GITHUB_PATH={ghPath}";
                exportStanzas += $" PATH={prepend}{pathSuffix}";

                input.Writer.TryWrite(exportStanzas + " " + file + " " + arguments_node);
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
