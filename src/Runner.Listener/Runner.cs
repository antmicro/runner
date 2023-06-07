using GitHub.DistributedTask.WebApi;
using GitHub.Runner.Listener.Configuration;
using GitHub.Runner.Common.Util;
using System;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Services.WebApi;
using Pipelines = GitHub.DistributedTask.Pipelines;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;

namespace GitHub.Runner.Listener
{
    [ServiceLocator(Default = typeof(Runner))]
    public interface IRunner : IRunnerService
    {
        Task<int> ExecuteCommand(CommandSettings command);
    }

    public sealed class Runner : RunnerService, IRunner
    {
        private IMessageListener[] _messageListeners;
        private IJobDispatcher _jobDispatcher;
        private ITerminal _term;
        private bool _inConfigStage;
        private ManualResetEvent _completedCommand = new ManualResetEvent(false);
        private bool _exiting = false;
        private int _returnCode = Constants.Runner.ReturnCode.Success;
        private CancellationTokenSource _messageQueueLoopTokenSource;
        private Task[] _deleteListenerSession;
        private bool _runOnceJobReceived = false;
        private SemaphoreSlim _selfUpdateSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim[] _ctrlCHandlerSemaphore = new SemaphoreSlim[Constants.AvailableRunnerInstances];
        private readonly EventWaitHandle[] _waitForMainLoop = new ManualResetEvent[Constants.AvailableRunnerInstances];

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _term = HostContext.GetService<ITerminal>();
            _deleteListenerSession = new Task[Constants.AvailableRunnerInstances];
            _messageListeners= HostContext.GetServiceArray<IMessageListener>();
            _jobDispatcher = HostContext.GetService<IJobDispatcher>();
            for (int i = 0; i < _ctrlCHandlerSemaphore.Length; ++i)
                _ctrlCHandlerSemaphore[i] = new SemaphoreSlim(0, 1);
            for (int i = 0; i < _waitForMainLoop.Length; ++i)
                _waitForMainLoop[i] = new ManualResetEvent(false);
        }

        public bool NumberProvided()
        {
            if (Environment.GetEnvironmentVariable(Constants.InstanceNumberVariable) == null)
            {
                _term.WriteError($"Please set the {Constants.InstanceNumberVariable} variable!");
                return false;
            }
            return true;
        }

        public async Task<int> ExecuteCommand(CommandSettings command)
        {
            try
            {
                VssUtil.InitializeVssClientSettings(HostContext.UserAgents, HostContext.WebProxy);

                _inConfigStage = true;
                _completedCommand.Reset();
                _term.CancelKeyPress += CtrlCHandler;

                //register a SIGTERM handler
                HostContext.Unloading += Runner_Unloading;

                // TODO Unit test to cover this logic
                Trace.Info(nameof(ExecuteCommand));
                var configManager = HostContext.GetService<IConfigurationManager>();

                // command is not required, if no command it just starts if configured

                // TODO: Invalid config prints usage

                if (command.Help)
                {
                    PrintUsage(command);
                    return Constants.Runner.ReturnCode.Success;
                }

                if (command.Version)
                {
                    _term.WriteLine(BuildConstants.RunnerPackage.Version);
                    return Constants.Runner.ReturnCode.Success;
                }

                if (command.Commit)
                {
                    _term.WriteLine(BuildConstants.Source.CommitHash);
                    return Constants.Runner.ReturnCode.Success;
                }

                // Configure runner prompt for args if not supplied
                // Unattended configure mode will not prompt for args if not supplied and error on any missing or invalid value.
                if (command.Configure)
                {
                    if (!NumberProvided())
                    {
                        return Constants.Runner.ReturnCode.TerminatedError;
                    }

                    try
                    {
                        await configManager.ConfigureAsync(command);
                        return Constants.Runner.ReturnCode.Success;
                    }
                    catch (Exception ex)
                    {
                        Trace.Error(ex);
                        _term.WriteError(ex.Message);
                        return Constants.Runner.ReturnCode.TerminatedError;
                    }
                }

                // remove config files, remove service, and exit
                if (command.Remove)
                {
                    try
                    {
                        await configManager.UnconfigureAsync(command);
                        return Constants.Runner.ReturnCode.Success;
                    }
                    catch (Exception ex)
                    {
                        Trace.Error(ex);
                        _term.WriteError(ex.Message);
                        return Constants.Runner.ReturnCode.TerminatedError;
                    }
                }

                _inConfigStage = false;

                // warmup runner process (JIT/CLR)
                // In scenarios where the runner is single use (used and then thrown away), the system provisioning the runner can call `Runner.Listener --warmup` before the machine is made available to the pool for use.
                // this will optimizes the runner process startup time.
                if (command.Warmup)
                {
                    var binDir = HostContext.GetDirectory(WellKnownDirectory.Bin);
                    foreach (var assemblyFile in Directory.EnumerateFiles(binDir, "*.dll"))
                    {
                        try
                        {
                            Trace.Info($"Load assembly: {assemblyFile}.");
                            var assembly = Assembly.LoadFrom(assemblyFile);
                            var types = assembly.GetTypes();
                            foreach (Type loadedType in types)
                            {
                                try
                                {
                                    Trace.Info($"Load methods: {loadedType.FullName}.");
                                    var methods = loadedType.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                                    foreach (var method in methods)
                                    {
                                        if (!method.IsAbstract && !method.ContainsGenericParameters)
                                        {
                                            Trace.Verbose($"Prepare method: {method.Name}.");
                                            RuntimeHelpers.PrepareMethod(method.MethodHandle);
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Trace.Error(ex);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Trace.Error(ex);
                        }
                    }

                    return Constants.Runner.ReturnCode.Success;
                }

                var store = HostContext.GetService<IConfigurationStore>();
                bool configuredAsService = store.IsServiceConfigured();

                // Run runner
                if (command.Run) // this line is current break machine provisioner.
                {
                    if (!NumberProvided())
                    {
                        return Constants.Runner.ReturnCode.TerminatedError;
                    }

                    // Error if runner not configured.
                    if (!configManager.IsConfigured())
                    {
                        _term.WriteError("Runner is not configured.");
                        PrintUsage(command);
                        return Constants.Runner.ReturnCode.TerminatedError;
                    }

                    Trace.Verbose($"Configured as service: '{configuredAsService}'");

                    //Get the startup type of the runner i.e., autostartup, service, manual
                    StartupType startType;
                    var startupTypeAsString = command.GetStartupType();
                    if (string.IsNullOrEmpty(startupTypeAsString) && configuredAsService)
                    {
                        // We need try our best to make the startup type accurate 
                        // The problem is coming from runner autoupgrade, which result an old version service host binary but a newer version runner binary
                        // At that time the servicehost won't pass --startuptype to Runner.Listener while the runner is actually running as service.
                        // We will guess the startup type only when the runner is configured as service and the guess will based on whether STDOUT/STDERR/STDIN been redirect or not
                        Trace.Info($"Try determine runner startup type base on console redirects.");
                        startType = (Console.IsErrorRedirected && Console.IsInputRedirected && Console.IsOutputRedirected) ? StartupType.Service : StartupType.Manual;
                    }
                    else
                    {
                        if (!Enum.TryParse(startupTypeAsString, true, out startType))
                        {
                            Trace.Info($"Could not parse the argument value '{startupTypeAsString}' for StartupType. Defaulting to {StartupType.Manual}");
                            startType = StartupType.Manual;
                        }
                    }

                    Trace.Info($"Set runner startup type - {startType}");
                    HostContext.StartupType = startType;

                    // Run the runner interactively or as service
                    return await RunAsync(command.RunOnce);
                }
                else
                {
                    PrintUsage(command);
                    return Constants.Runner.ReturnCode.Success;
                }
            }
            finally
            {
                _term.CancelKeyPress -= CtrlCHandler;
                HostContext.Unloading -= Runner_Unloading;
                _completedCommand.Set();
            }
        }

        private void Runner_Unloading(object sender, EventArgs e)
        {
            if ((!_inConfigStage) && (!HostContext.RunnerShutdownToken.IsCancellationRequested))
            {
                HostContext.ShutdownRunner(ShutdownReason.UserCancelled);
                _completedCommand.WaitOne(Constants.Runner.ExitOnUnloadTimeout);
            }
        }

        private void CtrlCHandler(object sender, EventArgs e)
        {
            _term.WriteLine("Exiting...");
            if (_inConfigStage)
            {
                HostContext.Dispose();
                Environment.Exit(Constants.Runner.ReturnCode.InterruptSignal);
            }
            else if (!_exiting)
            {
                _exiting = true;
                _returnCode = Constants.Runner.ReturnCode.InterruptSignal;
                Parallel.For(0, _messageListeners.Length, runnerId => {
                    Trace.Info($"Handler for Runner {runnerId}, waiting for main loop...");
                    _waitForMainLoop[runnerId].WaitOne();
                    try {
                        if (_jobDispatcher.Busy[runnerId])
                        {
                            Trace.Info($"Job is running on Runner {runnerId}");
                            _deleteListenerSession[runnerId] = _messageListeners[runnerId].DeleteSessionAsync();
                            _deleteListenerSession[runnerId].GetAwaiter().GetResult();
                        }
                        else
                        {
                            _deleteListenerSession[runnerId] = _messageListeners[runnerId].DeleteSessionAsync();
                            Trace.Info($"Runner {runnerId} is Idle, deleting session");
                            _deleteListenerSession[runnerId].GetAwaiter().GetResult();

                            var server = HostContext.GetServiceArray<IRunnerServer>()[runnerId];
                            var setting = HostContext.GetService<IConfigurationStore>().GetSettings(runnerId);
                            TaskAgent currentAgent;
                            do {
                                Trace.Info($"Checking if server knows Runner {runnerId} is offline...");
                                currentAgent = server.GetAgentAsync(setting.PoolId, setting.AgentId).GetAwaiter().GetResult();
                                Task.Delay(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                            } while (currentAgent.Status == TaskAgentStatus.Online);

                            currentAgent = server.GetAgentAsync(setting.PoolId, setting.AgentId, includeAssignedRequest: true).GetAwaiter().GetResult();
                            Trace.Info($"Checking if Runner {runnerId} has some job assigned: {currentAgent.AssignedRequest}");

                            if (currentAgent.AssignedRequest != null && !_jobDispatcher.Busy[runnerId])
                            {
                                Trace.Info($"Job is assigned, run Listener {runnerId} once more only for this job");
                                ListenerLoopAsync(_messageListeners[runnerId], runnerId, false, true).GetAwaiter().GetResult();
                            }
                        }
                    } finally {
                        Trace.Info($"Releasing semaphore {runnerId}");
                        _ctrlCHandlerSemaphore[runnerId].Release();
                    }
                });
            }
        }

        private async Task<int> ListenerLoopAsync(IMessageListener messageListener, int runnerId, bool runOnce = false, bool deleteSessionAfterJobMessage = false)
        {
            Task<TaskAgentMessage> getNextMessage;
            TaskAgentMessage message;
            bool skipMessageDeletion;
            bool autoUpdateInProgress = false;

            Trace.Entering(nameof(ListenerLoopAsync));
            if (!await messageListener.CreateSessionAsync(runnerId, HostContext.RunnerShutdownToken))
            {
                return Constants.Runner.ReturnCode.TerminatedError;
            }

            try {
                _waitForMainLoop[runnerId].Set();
                while (!HostContext.RunnerShutdownToken.IsCancellationRequested && !_messageQueueLoopTokenSource.IsCancellationRequested)
                {
                    message = null;
                    skipMessageDeletion = false;
                    try
                    {
                        getNextMessage = messageListener.GetNextMessageAsync(_messageQueueLoopTokenSource.Token);
                        if (autoUpdateInProgress)
                        {
                            Trace.Verbose("Auto update task running at backend, waiting for getNextMessage or selfUpdateTask to finish.");

                            autoUpdateInProgress = false;

                            _messageQueueLoopTokenSource.Cancel();
                            try
                            {
                                await getNextMessage;
                            }
                            catch (Exception ex)
                            {
                                Trace.Info($"Ignore any exception after cancel message loop. {ex}");
                            }

                            if (!deleteSessionAfterJobMessage)
                                _ctrlCHandlerSemaphore[runnerId].Release();
                            if (runOnce)
                            {
                                return Constants.Runner.ReturnCode.RunOnceRunnerUpdating;
                            }
                            else
                            {
                                return Constants.Runner.ReturnCode.RunnerUpdating;
                            }
                        }

                        if (_runOnceJobReceived)
                        {
                            Trace.Verbose("One time used runner has start running its job, waiting for getNextMessage or the job to finish.");
                            Task completeTask = await Task.WhenAny(
                                getNextMessage,
                                _jobDispatcher.RunOnceJobCompleted.Task
                            );
                            if (!Object.ReferenceEquals(completeTask, getNextMessage))
                            {
                                Trace.Info("Job has finished at backend, the runner will exit since it is running under onetime use mode.");
                                Trace.Info("Stop message queue looping.");
                                _messageQueueLoopTokenSource.Cancel();
                                try
                                {
                                    await getNextMessage;
                                }
                                catch (Exception ex)
                                {
                                    Trace.Info($"Ignore any exception after cancel message loop. {ex}");
                                }

                                if (!deleteSessionAfterJobMessage)
                                    _ctrlCHandlerSemaphore[runnerId].Release();
                                return Constants.Runner.ReturnCode.Success;
                            }
                        }

                        try {
                            message = await getNextMessage; //get next message
                        } catch (Exception ex) when (
                            ex is OperationCanceledException || ex is AccessDeniedException)
                        {
                            // getNextMessage was cancelled
                            skipMessageDeletion = true;
                            break;
                        }
                        HostContext.WritePerfCounter($"MessageReceived_{message.MessageType}");
                        if (string.Equals(message.MessageType, AgentRefreshMessage.MessageType, StringComparison.OrdinalIgnoreCase))
                        {
                            var initRunnerVersion = messageListener.GetInitRunnerVersion();

                            if (autoUpdateInProgress == false)
                            {
                                var runnerUpdateMessage = JsonUtility.FromString<AgentRefreshMessage>(message.Body);
                                Trace.Info($"Init RV: {initRunnerVersion}, current RV: {runnerUpdateMessage.TargetVersion}");
                                if (initRunnerVersion != runnerUpdateMessage.TargetVersion)
                                {
                                    await _selfUpdateSemaphore.WaitAsync();
                                    autoUpdateInProgress = true;
                                    try
                                    {
                                        FileStream fileStream = new FileStream("../src/current_runnerversion", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                                        using (StreamWriter sr = new StreamWriter(fileStream))
                                        {
                                            sr.Write(runnerUpdateMessage.TargetVersion);
                                        }
                                    }
                                    catch (Exception ex) {
                                        Trace.Info($"Ignore exception on version write: {ex}");
                                    }
                                    finally {
                                        _selfUpdateSemaphore.Release();
                                    }
                                    Trace.Info("Refresh message received, will restart the runner.");
                                }
                                else
                                {
                                    Trace.Info("Refresh message received but RV seems up-to-date.");
                                }
                            }
                            else
                            {
                                Trace.Info("Refresh message received but variable had already been flipped.");
                            }

                        }
                        else if (string.Equals(message.MessageType, JobRequestMessageTypes.PipelineAgentJobRequest, StringComparison.OrdinalIgnoreCase))
                        {
                            if (autoUpdateInProgress || _runOnceJobReceived)
                            {
                                skipMessageDeletion = true;
                                Trace.Info($"Skip message deletion for job request message '{message.MessageId}'.");
                            }
                            else
                            {
                                var jobMessage = StringUtil.ConvertFromJson<Pipelines.AgentJobRequestMessage>(message.Body);
                                _jobDispatcher.Run(jobMessage, runnerId, runOnce);
                                if (runOnce)
                                {
                                    Trace.Info("One time used runner received job message.");
                                    _runOnceJobReceived = true;
                                }
                                if (deleteSessionAfterJobMessage)
                                {
                                    Trace.Info("Loop: deleting session");
                                    _deleteListenerSession[runnerId] = null;
                                    break;
                                }
                            }
                        }
                        else if (string.Equals(message.MessageType, JobCancelMessage.MessageType, StringComparison.OrdinalIgnoreCase))
                        {
                            var cancelJobMessage = JsonUtility.FromString<JobCancelMessage>(message.Body);
                            bool jobCancelled = _jobDispatcher.Cancel(cancelJobMessage);
                            skipMessageDeletion = (autoUpdateInProgress || _runOnceJobReceived) && !jobCancelled;

                            if (skipMessageDeletion)
                            {
                                Trace.Info($"Skip message deletion for cancellation message '{message.MessageId}'.");
                            }
                        }
                        else
                        {
                            Trace.Error($"Received message {message.MessageId} with unsupported message type {message.MessageType}.");
                        }
                    }
                    finally
                    {
                        if (!skipMessageDeletion && message != null)
                        {
                            try
                            {
                                await messageListener.DeleteMessageAsync(message);
                            }
                            catch (Exception ex)
                            {
                                Trace.Error($"Catch exception during delete message from message queue. message id: {message.MessageId}");
                                Trace.Error(ex);
                            }
                            finally
                            {
                                message = null;
                            }
                        }
                    }
                }
                return Constants.Runner.ReturnCode.Success;
            }
            catch (Exception) {
                if (!deleteSessionAfterJobMessage)
                    _ctrlCHandlerSemaphore[runnerId].Release();
                throw;
            }
            finally {
                try {
                    await ((_deleteListenerSession[runnerId] == null) ? messageListener.DeleteSessionAsync() : _deleteListenerSession[runnerId]);
                } catch (AccessDeniedException) {
                    Trace.Warning($"Access Denied exception during closing listeners {runnerId}");
                }

                if (!deleteSessionAfterJobMessage)
                {   // wait for end of handler only if method was call outside of this handler
                    Trace.Info($"Waiting for CtrlC handler for Runner {runnerId}.");
                    await _ctrlCHandlerSemaphore[runnerId].WaitAsync();
                    Trace.Info($"CtrlC handler for Runner {runnerId} finished.");
                }
            }
        }

        /// <summary>
        /// executes listener loops for runners in parallel
        /// </summary>
        private async Task<int> RunAsync(bool runOnce = false)
        {
            try
            {
                Trace.Entering(nameof(RunAsync));

                var configurationStore = HostContext.GetService<IConfigurationStore>();
                var jobNotifications = HostContext.GetServiceArray<IJobNotification>();
                for (int i = 0; i < jobNotifications.Length; ++i)
                    jobNotifications[i].StartClient(configurationStore.GetSettings(i).MonitorSocketAddress);

                HostContext.WritePerfCounter("SessionCreated");
                _term.WriteLine($"{DateTime.UtcNow:u}: Listening for Jobs");

                _messageQueueLoopTokenSource = CancellationTokenSource.CreateLinkedTokenSource(HostContext.RunnerShutdownToken);

                try
                {
                    Parallel.For(0, _messageListeners.Length, i => {
                        Trace.Info($"Start loop for runnerId={i}");
                        var loop = ListenerLoopAsync(_messageListeners[i], i, runOnce);
                        try {
                            Task.WaitAll(loop);
                        } finally {
                            if (loop.IsCompleted && loop.Result != Constants.Runner.ReturnCode.Success)
                                _returnCode = loop.Result;
                        }
                        Trace.Info($"Return code of loop for Runner {i}: {loop.Result}");
                    });
                    Trace.Info("End Loop");
                }
                finally
                {
                    //TODO: make sure we don't mask more important exception
                    Trace.Info("Waiting for jobs to end");
                    await (await _jobDispatcher.WaitForCompletion());
                    _jobDispatcher.ShutdownAsync();

                    _messageQueueLoopTokenSource.Dispose();
                }
            }
            catch (TaskAgentAccessTokenExpiredException)
            {
                Trace.Info("Runner OAuth token has been revoked. Shutting down.");
            }

            // Make sure Runner won't exit before the right return code is set
            // Trace.Info($"Exiting Listener RunAsync {result}, {_returnCode}");
            return _returnCode;
        }

        private void PrintUsage(CommandSettings command)
        {
            string separator;
            string ext;
#if OS_WINDOWS
            separator = "\\";
            ext = "cmd";
#else
            separator = "/";
            ext = "sh";
#endif
            _term.WriteLine($@"
Commands:
 .{separator}config.{ext}         Configures the runner
 .{separator}config.{ext} remove  Unconfigures the runner
 .{separator}run.{ext}            Runs the runner interactively. Does not require any options.

Options:
 --help     Prints the help for each command
 --version  Prints the runner version
 --commit   Prints the runner commit

Config Options:
 --unattended           Disable interactive prompts for missing arguments. Defaults will be used for missing options
 --url string           Repository to add the runner to. Required if unattended
 --token string         Registration token. Required if unattended
 --name string          Name of the runner to configure (default {Environment.MachineName ?? "myrunner"})
 --runnergroup string   Name of the runner group to add this runner to (defaults to the default runner group)
 --labels string        Extra labels in addition to the default: 'self-hosted,{Constants.Runner.Platform},{Constants.Runner.PlatformArchitecture}'
 --work string          Relative runner work directory (default {Constants.Path.WorkDirectory})
 --replace              Replace any existing runner with the same name (default false)");
#if OS_WINDOWS
    _term.WriteLine($@" --runasservice   Run the runner as a service");
    _term.WriteLine($@" --windowslogonaccount string   Account to run the service as. Requires runasservice");
    _term.WriteLine($@" --windowslogonpassword string  Password for the service account. Requires runasservice");
#endif
    _term.WriteLine($@"
Examples:
 Configure a runner non-interactively:
  .{separator}config.{ext} --unattended --url <url> --token <token>
 Configure a runner non-interactively, replacing any existing runner with the same name:
  .{separator}config.{ext} --unattended --url <url> --token <token> --replace [--name <name>]
 Configure a runner non-interactively with three extra labels:
  .{separator}config.{ext} --unattended --url <url> --token <token> --labels L1,L2,L3");
#if OS_WINDOWS
    _term.WriteLine($@" Configure a runner to run as a service:");
    _term.WriteLine($@"  .{separator}config.{ext} --url <url> --token <token> --runasservice");
#endif
        }

    }
}
