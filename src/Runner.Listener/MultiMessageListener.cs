using GitHub.DistributedTask.WebApi;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Runner.Common;

namespace GitHub.Runner.Listener
{
    [ServiceLocator(Default = typeof(MultiMessageListener))]
    public interface IMultiMessageListener : IRunnerService
    {
        public IMessageListener[] MessageListeners { get; }
		
	    Task<int> CreateSessionsAsync(IHostContext hostContext);
		Task<MessageForRunner?> GetNextMessage(CancellationToken token);
		void InitTasks(CancellationToken token);
		Task DeleteSessionsAsync();
		Task WaitForAllMessages();

		string GetInitRunnerVersion();
    }
	
    public class MultiMessageListener : IMultiMessageListener
    {
		public IHostContext hostContext;
        private IMessageListener[] _messageLiteners;
        private Task<TaskAgentMessage>[] _tasks;

        public IMessageListener[] MessageListeners
        {
            get => _messageLiteners;
        }

        public void Initialize(IHostContext hostContext)
        {
			this.hostContext = hostContext;

            _messageLiteners = new IMessageListener[Constants.AvailableRunnerInstances];
            _tasks = new Task<TaskAgentMessage>[Constants.AvailableRunnerInstances];
        }

        public async Task<int> CreateSessionsAsync(IHostContext hostContext)
        {
            int code = 0;
            for (int i = 0; i < _messageLiteners.Length; ++i)
            {
                _messageLiteners[i] = hostContext.CreateService<IMessageListener>();
                if (!await _messageLiteners[i].CreateSessionAsync(i, hostContext.RunnerShutdownToken))
                {
                    code = Constants.Runner.ReturnCode.TerminatedError;
                }
            }
            return code;
        }

        public async Task<MessageForRunner?> GetNextMessage(CancellationToken token)
        {
            var task = await Task.WhenAny<TaskAgentMessage>(_tasks);
            if (task.IsCanceled)
                return null;
            TaskAgentMessage message = task.Result;

            int id = Array.FindIndex<Task<TaskAgentMessage>>(_tasks, t => Object.ReferenceEquals(task, t));
            _tasks[id] = _messageLiteners[id].GetNextMessageAsync(token);

            return new MessageForRunner {
                RunnerId = id,
                Message = message
            };
        }

        public void InitTasks(CancellationToken token)
        {
            for (int i = 0; i < Constants.AvailableRunnerInstances; ++i)
            {
                if (_tasks[i] == null)
                    _tasks[i] = _messageLiteners[i].GetNextMessageAsync(token);
            }
        }

        public Task DeleteSessionsAsync()
        {
            return Task.WhenAll(
                from listener in _messageLiteners select listener.DeleteSessionAsync()
            );
        }

        public string GetInitRunnerVersion()
        {
            return _messageLiteners[0].GetInitRunnerVersion();
        }

        public Task WaitForAllMessages()
        {
            return Task.WhenAll(_tasks);
        }
    }

    public struct MessageForRunner
    {
        public int RunnerId;
        public TaskAgentMessage Message;
    }

}
