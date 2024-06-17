using System;
using System.IO;
using System.Threading.Tasks;

namespace GitHub.Runner.Common
{
    [ServiceLocator(Default = typeof(PagingLogger))]
    public interface IPagingLogger : IRunnerService
    {
        long TotalLines { get; }
        void Setup(Guid timelineId, Guid timelineRecordId, Func<string, bool, bool, Task> uploadLogs = null);

        void Write(string message);

        void End();

        int PageCount { get; }
    }

    public class SecretLogger : RunnerService, IPagingLogger
    {
        public static string PagingFolder = "pages";
        // 8 MB
        public const int PageSize = 8 * 1024 * 1024;
        // For Results
        public static string BlocksFolder = "blocks";
        // 2 MB
        public const int BlockSize = 2 * 1024 * 1024;
        
        protected bool _removeLogsAfterUploadBucket = true;

        protected Guid _timelineId;
        protected Guid _timelineRecordId;
        protected FileStream _pageData;
        protected StreamWriter _pageWriter;
        protected int _byteCount;
        protected int _pageCount;
        protected long _totalLines;
        protected string _dataFileName;
        protected string _pagesFolder;
        protected IJobServerQueue _jobServerQueue;

        protected Func<string, bool, bool, Task> _uploadLogs;

        protected string _resultsDataFileName;
        protected FileStream _resultsBlockData;
        protected StreamWriter _resultsBlockWriter;
        protected string _resultsBlockFolder;
        protected int _blockByteCount;
        protected int _blockCount;

        public long TotalLines => _totalLines;
        public int PageCount => _pageCount;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _totalLines = 0;
            _pagesFolder = Path.Combine(hostContext.GetDirectory(WellKnownDirectory.Diag), PagingFolder);
            Directory.CreateDirectory(_pagesFolder);
            _resultsBlockFolder = Path.Combine(hostContext.GetDirectory(WellKnownDirectory.Diag), BlocksFolder);
            Directory.CreateDirectory(_resultsBlockFolder);
            _jobServerQueue = HostContext.GetService<IJobServerQueue>();
        }

        public void Setup(Guid timelineId, Guid timelineRecordId, Func<string, bool, bool, Task> uploadLogs = null)
        {
            _timelineId = timelineId;
            _timelineRecordId = timelineRecordId;
            _uploadLogs = uploadLogs;
        }

        //
        // Write a metadata file with id etc, point to pages on disk.
        // Each page is a guid_#.  As a page rolls over, it events it's done
        // and the consumer queues it for upload
        // Ensure this is lazy.  Create a page on first write
        //
        public void Write(string message)
        {
            // lazy creation on write
            if (_pageWriter == null)
            {
                NewPage();
            }

            if (_resultsBlockWriter == null)
            {
                NewBlock();
            }

            string line = $"{DateTime.UtcNow.ToString("O")} {message}";
            _pageWriter.WriteLine(line);
            _resultsBlockWriter.WriteLine(line);

            _totalLines++;
            if (line.IndexOf('\n') != -1)
            {
                foreach (char c in line)
                {
                    if (c == '\n')
                    {
                        _totalLines++;
                    }
                }
            }

            var bytes = System.Text.Encoding.UTF8.GetByteCount(line);
            _byteCount += bytes;
            _blockByteCount += bytes;
            if (_byteCount >= PageSize)
            {
                NewPage();
            }

            if (_blockByteCount >= BlockSize)
            {
                NewBlock();
            }

        }

        public void End()
        {
            EndPage();
            EndBlock(true);
        }

        protected virtual string LogFileName()
        {
            return $"{_timelineId}_{_timelineRecordId}_secret_{_pageCount}.log";
        }
        
        protected virtual string BlockFileName()
        {
            return $"{_timelineId}_{_timelineRecordId}_secret.{++_blockCount}";
        }
        
        private void NewPage()
        {
            EndPage();
            _byteCount = 0;
            ++_pageCount;
            _dataFileName = Path.Combine(_pagesFolder, LogFileName());
            _pageData = new FileStream(_dataFileName, FileMode.CreateNew);
            _pageWriter = new StreamWriter(_pageData, System.Text.Encoding.UTF8);
        }

        protected virtual void EndPage(bool secret = true)
        {
            if (_pageWriter != null)
            {
                _pageWriter.Flush();
                _pageData.Flush();
                //The StreamWriter object calls Dispose() on the provided Stream object when StreamWriter.Dispose is called.
                _pageWriter.Dispose();
                _pageWriter = null;
                _pageData = null;

                _uploadLogs(_dataFileName, _removeLogsAfterUploadBucket, secret).GetAwaiter().GetResult();
            }
        }

        private void NewBlock()
        {
            EndBlock(false);
            _blockByteCount = 0;
            _resultsDataFileName = Path.Combine(_resultsBlockFolder, BlockFileName());
            _resultsBlockData = new FileStream(_resultsDataFileName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            _resultsBlockWriter = new StreamWriter(_resultsBlockData, System.Text.Encoding.UTF8);
        }

        private void EndBlock(bool finalize)
        {
            if (_resultsBlockWriter != null)
            {
                _resultsBlockWriter.Flush();
                _resultsBlockData.Flush();
                _resultsBlockWriter.Dispose();
                _resultsBlockWriter = null;
                _resultsBlockData = null;
                _jobServerQueue.QueueResultsUpload(_timelineRecordId, "ResultsLog", _resultsDataFileName, "Results.Core.Log", deleteSource: true, finalize, firstBlock: _resultsDataFileName.EndsWith(".1"), totalLines: _totalLines);
            }
        }
    }

    public class PagingLogger : SecretLogger
    {
        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);
            _jobServerQueue = HostContext.GetService<IJobServerQueue>();
            _removeLogsAfterUploadBucket = false;
        }

        protected override void EndPage(bool secret = false)
        {
            if (_pageWriter != null)
            {
                base.EndPage(false);
                _jobServerQueue.QueueFileUpload(_timelineId, _timelineRecordId, "DistributedTask.Core.Log", "CustomToolLog", _dataFileName, true);
            }
        }
        
        protected override string LogFileName()
        {
            return $"{_timelineId}_{_timelineRecordId}_{_pageCount}.log";
        }
        
        protected override string BlockFileName()
        {
            return $"{_timelineId}_{_timelineRecordId}.{++_blockCount}";
        }
    }
}
