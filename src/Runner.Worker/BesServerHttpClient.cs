using System;
using System.IO;
using System.Dynamic;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using GitHub.Runner.Common;

namespace GitHub.Runner.Worker {

    [ServiceLocator(Default = typeof(BesServerHttpClient))]
	public interface IBesServerHttpClient : IRunnerService {
		public string LastInvocationId { get; }
		public bool ServerExists { get; }

		public string GenerateInvocationId(GitHubContext context);

		public Task CreateInvocation(GitHubContext context);
		public Task AddInvocationLog(GitHubContext context, string log);
		public Task AddTarget(GitHubContext context, string token);
		public Task AddRunInformation(GitHubContext context);
		public Task AddTargetLog(GitHubContext context, string logFile);
		public Task AddTargetArtifact(GitHubContext context, string artifactFile);
		public Task DeleteTarget(GitHubContext context, int status = 1, long duration = 1000);
	}

    public class BesServerHttpClient : IBesServerHttpClient
    {
		// Endpoints
		private static readonly string HOSTNAME = "http://localhost";
		private static readonly Uri INVOCATION = new Uri(HOSTNAME + "/invocation");
		private static readonly Uri LOGS = new Uri(INVOCATION + "/logs");
		private static readonly Uri TARGET = new Uri(INVOCATION + "/target");
		private static readonly Uri TARGET_LOG = new Uri(TARGET + "/log");
		private static readonly Uri ARTIFACT = new Uri(TARGET + "/artifact");

		private static readonly string RUN_INFORMATION_TEMPLATE = "Repository:\t{0}\nCommit:\t\t{1}\nAuthor name:\t{2}\nAuthor email:\t{3}\nCommit message:\t{4}\n\nGitHub Actions run URL: {5}";

		private HttpClient _httpClient;
		private readonly int _retries = 5;
		private Tracing Trace { get; set; }
		public string LastInvocationId { get; private set; }

        public void Initialize(IHostContext context)
        {
			Trace = context.GetTrace(nameof(BesServerHttpClient));
			var socket = GCP.GCPCoordinator.GetGcpBuildResultViewerUrl(context);
			Connect(Constants.DistantBesSocket);
        }

		private bool Connect(string socketPath)
		{
			if (string.IsNullOrEmpty(socketPath) || !File.Exists(socketPath))
				return false;
			_httpClient = new HttpClient(new SocketsHttpHandler {
				ConnectCallback = async (context, token) => {
					var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
					var endpoint = new UnixDomainSocketEndPoint(socketPath);
					await socket.ConnectAsync(endpoint);
					return new NetworkStream(socket, ownsSocket: true);
				}
			});
			return true;
		}

		public bool ServerExists
		{
			get => _httpClient != null;
		}

		public async Task CreateInvocation(GitHubContext context)
		{
			dynamic json = new ExpandoObject();
			json.repo = context["repository"];
			json.sha = context["sha"];
			json.run_id = context["run_id"];
			json.run_attempt = context["run_attempt"];
			await SendAsync(INVOCATION, HttpMethod.Post, json, context);
		}

		public async Task AddRunInformation(GitHubContext context)
		{
			var info = string.Format(RUN_INFORMATION_TEMPLATE, context["repository"], context["sha"], context["author_name"], context["author_email"], context["commit_message"], $"{context["repository_url"]}/actions/runs/{context["run_id"]}/attempts/{context["run_attempt"]}");
			await AddInvocationLog(context, info);
		}

		public async Task AddInvocationLog(GitHubContext context, string log)
		{
			dynamic json = new ExpandoObject();
			json.logs = log;
			await SendAsync(LOGS, HttpMethod.Post, json, context);
		}

		public async Task AddTarget(GitHubContext context, string token)
		{
			dynamic json = new ExpandoObject();
			json.target_name = context["job_display_name"].ToString();
			json.token = token;
			await SendAsync(TARGET, HttpMethod.Post, json, context);
		}

		public async Task AddTargetLog(GitHubContext context, string logFile)
		{
			dynamic json = new ExpandoObject();
			json.target_name = context["job_display_name"].ToString();
			json.log_file = Path.GetFullPath(logFile);
			await SendAsync(TARGET_LOG, HttpMethod.Post, json, context);
		}

		public async Task AddTargetArtifact(GitHubContext context, string artifactFile)
		{
			dynamic json = new ExpandoObject();
			json.target_name = context["job_display_name"].ToString();
			json.artifact = Path.GetFullPath(artifactFile);
			await SendAsync(ARTIFACT, HttpMethod.Post, json, context);
		}

		public async Task DeleteTarget(GitHubContext context, int status = 1, long duration = 1000)
		{
			dynamic json = new ExpandoObject();
			json.target_name = context["job_display_name"].ToString();
			json.status = status;
			json.duration = duration;
			await SendAsync(TARGET, HttpMethod.Delete, json, context);
		}

		public string GenerateInvocationId(GitHubContext context)
		{
			return $"{context["repository"]}-{context["run_number"]}-{context["run_attempt"]}".Replace(' ', '_').Replace('/', '-');
		}

		private async Task<HttpResponseMessage> SendAsync(Uri endpoint, HttpMethod method, dynamic content, GitHubContext context)
		{
			if (_httpClient == null && !Connect(Constants.DistantBesSocket)) {
				Trace.Info("BRV HTTP client has not been specified, refusing to send the message");
				return null;
			}
			content.id = GenerateInvocationId(context);
			using (var request = new HttpRequestMessage()) {
				request.Method = method;
				request.RequestUri = endpoint;
				var jsonString = JsonConvert.SerializeObject(content);
				var body = new StringContent(jsonString);
				body.Headers.Clear();
				body.Headers.Add("Content-Type", "application/json");
				request.Content = body;
				Trace.Info(request);
				Trace.Info(jsonString);

				var retries = _retries;
				HttpResponseMessage response;
				while (retries-- > 0)
				{
					try {
						response = await _httpClient.SendAsync(request);
						response.EnsureSuccessStatusCode();
					} catch (Exception ex) {
						Trace.Error(ex);
						Trace.Info($"Remains {retries} chances.");
						continue;
					}
					var cont = JObject.Parse(await response.Content.ReadAsStringAsync());
					try {
						LastInvocationId = cont["invocation_id"].ToString();
					} catch (KeyNotFoundException) {}
					return response;
				}
				return null;
			}
		}
    }

}