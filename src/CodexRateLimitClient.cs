using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexQuotaTray
{
    internal sealed class CodexRateLimitClient : IDisposable
    {
        private sealed class Connection
        {
            public Process Process;
            public StreamWriter Writer;
            public readonly object WriteLock = new object();
            public int InitializeId;
            public volatile bool Initialized;
            public DateTime LastRequestUtc;
            public volatile bool SawSnapshot;
        }

        private readonly object _connectionLock = new object();
        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private readonly AutoResetEvent _refreshEvent = new AutoResetEvent(false);
        private Thread _worker;
        private Connection _connection;
        private int _nextRequestId;
        private bool _disposed;

        public event Action<QuotaSnapshot> SnapshotReceived;
        public event Action<string> StatusChanged;

        public void Start()
        {
            if (_worker != null)
            {
                return;
            }

            _worker = new Thread(WorkerLoop);
            _worker.Name = "Codex quota app-server";
            _worker.IsBackground = true;
            _worker.Start();
        }

        public void RequestNow()
        {
            Connection connection;
            lock (_connectionLock)
            {
                connection = _connection;
            }

            if (connection != null && connection.Initialized)
            {
                RequestQuota(connection, true);
            }
            else
            {
                EmitStatus("正在连接 Codex 实时接口…");
                _refreshEvent.Set();
            }
        }

        private void WorkerLoop()
        {
            int failureDelayMilliseconds = 3000;
            while (!_stopEvent.WaitOne(0))
            {
                bool successful = false;
                try
                {
                    string executable = CodexLocator.FindExecutable();
                    if (String.IsNullOrEmpty(executable))
                    {
                        EmitStatus("未找到 Codex，使用本地缓存");
                        int missingWait = WaitHandle.WaitAny(
                            new WaitHandle[] { _stopEvent, _refreshEvent },
                            30000);
                        if (missingWait == 0)
                        {
                            break;
                        }

                        continue;
                    }

                    successful = RunConnection(executable);
                }
                catch
                {
                    EmitStatus("Codex 实时接口暂不可用，使用本地缓存");
                }

                int waitResult = WaitHandle.WaitAny(
                    new WaitHandle[] { _stopEvent, _refreshEvent },
                    successful ? 60000 : failureDelayMilliseconds);
                if (waitResult == 0)
                {
                    break;
                }

                failureDelayMilliseconds = successful
                    ? 3000
                    : Math.Min(60000, failureDelayMilliseconds * 2);
            }
        }

        private bool RunConnection(string executable)
        {
            Connection connection = new Connection();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = executable;
            startInfo.Arguments = "app-server --stdio";
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardInput = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = new UTF8Encoding(false);
            startInfo.StandardErrorEncoding = new UTF8Encoding(false);

            string codexHome = CodexLocator.ResolveCodexHome();
            if (!String.IsNullOrEmpty(codexHome))
            {
                startInfo.EnvironmentVariables["CODEX_HOME"] = codexHome;
            }

            Process process = new Process();
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;
            connection.Process = process;

            DataReceivedEventHandler outputHandler = delegate(object sender, DataReceivedEventArgs args)
            {
                if (!String.IsNullOrEmpty(args.Data))
                {
                    OnOutputLine(connection, args.Data);
                }
            };
            DataReceivedEventHandler errorHandler = delegate(object sender, DataReceivedEventArgs args)
            {
                // stderr is deliberately drained but never persisted; it can contain local paths.
            };

            process.OutputDataReceived += outputHandler;
            process.ErrorDataReceived += errorHandler;

            try
            {
                EmitStatus("正在连接 Codex 实时接口…");
                if (!process.Start())
                {
                    return false;
                }

                connection.Writer = process.StandardInput;
                connection.Writer.AutoFlush = true;
                lock (_connectionLock)
                {
                    _connection = connection;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                connection.InitializeId = Interlocked.Increment(ref _nextRequestId);
                string initialize = "{\"method\":\"initialize\",\"id\":" +
                    connection.InitializeId.ToString() +
                    ",\"params\":{\"clientInfo\":{\"name\":\"codex_quota_tray\",\"title\":\"Codex Quota Tray\",\"version\":\"1.0.0\"}}}";
                Send(connection, initialize);
                DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(15);

                while (!_stopEvent.WaitOne(500))
                {
                    if (HasExited(process))
                    {
                        break;
                    }

                    if (connection.SawSnapshot || DateTime.UtcNow >= deadlineUtc)
                    {
                        break;
                    }
                }

                return connection.SawSnapshot;
            }
            finally
            {
                lock (_connectionLock)
                {
                    if (Object.ReferenceEquals(_connection, connection))
                    {
                        _connection = null;
                    }
                }

                try
                {
                    if (connection.Writer != null)
                    {
                        connection.Writer.Close();
                    }
                }
                catch
                {
                }

                try
                {
                    if (!HasExited(process))
                    {
                        bool exitedCleanly = process.WaitForExit(1000);
                        if (!exitedCleanly)
                        {
                            process.Kill();
                            process.WaitForExit(2000);
                        }
                    }
                }
                catch
                {
                }

                process.OutputDataReceived -= outputHandler;
                process.ErrorDataReceived -= errorHandler;
                process.Dispose();
            }
        }

        private void OnOutputLine(Connection connection, string line)
        {
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 4 * 1024 * 1024;
                IDictionary<string, object> root = serializer.DeserializeObject(line) as IDictionary<string, object>;
                if (root == null)
                {
                    return;
                }

                string method = QuotaJsonParser.GetString(root, "method");
                if (String.Equals(method, "account/rateLimits/updated", StringComparison.Ordinal))
                {
                    RequestQuota(connection, false);
                    return;
                }

                int? id = QuotaJsonParser.GetNullableInt(root, "id");
                IDictionary<string, object> result = QuotaJsonParser.AsDictionary(QuotaJsonParser.GetValue(root, "result"));
                if (id.HasValue && id.Value == connection.InitializeId && result != null)
                {
                    Send(connection, "{\"method\":\"initialized\",\"params\":{}}");
                    connection.Initialized = true;
                    EmitStatus("Codex 实时接口已连接");
                    RequestQuota(connection, true);
                    return;
                }

                if (result != null)
                {
                    QuotaSnapshot snapshot = QuotaJsonParser.ParseAppServerResult(result, DateTime.UtcNow);
                    if (snapshot != null)
                    {
                        connection.SawSnapshot = true;
                        EmitSnapshot(snapshot);
                        EmitStatus("实时额度已更新");
                    }
                }

                if (QuotaJsonParser.GetValue(root, "error") != null)
                {
                    EmitStatus("实时额度读取失败，使用本地缓存");
                }
            }
            catch
            {
                // Ignore malformed/non-protocol output and keep the stream alive.
            }
        }

        private void RequestQuota(Connection connection, bool force)
        {
            DateTime now = DateTime.UtcNow;
            lock (connection.WriteLock)
            {
                if (!force && now - connection.LastRequestUtc < TimeSpan.FromSeconds(1))
                {
                    return;
                }

                connection.LastRequestUtc = now;
                int id = Interlocked.Increment(ref _nextRequestId);
                SendLocked(connection, "{\"method\":\"account/rateLimits/read\",\"id\":" + id.ToString() + "}");
            }
        }

        private static void Send(Connection connection, string message)
        {
            lock (connection.WriteLock)
            {
                SendLocked(connection, message);
            }
        }

        private static void SendLocked(Connection connection, string message)
        {
            try
            {
                if (connection.Writer != null)
                {
                    connection.Writer.WriteLine(message);
                    connection.Writer.Flush();
                }
            }
            catch
            {
            }
        }

        private static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch { return true; }
        }

        private void EmitSnapshot(QuotaSnapshot snapshot)
        {
            Action<QuotaSnapshot> handler = SnapshotReceived;
            if (handler != null)
            {
                handler(snapshot);
            }
        }

        private void EmitStatus(string status)
        {
            Action<string> handler = StatusChanged;
            if (handler != null)
            {
                handler(status);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopEvent.Set();
            Connection connection;
            lock (_connectionLock)
            {
                connection = _connection;
            }

            if (connection != null)
            {
                try { connection.Writer.Close(); }
                catch { }
                try
                {
                    if (!HasExited(connection.Process))
                    {
                        connection.Process.Kill();
                    }
                }
                catch { }
            }

            if (_worker != null && Thread.CurrentThread != _worker)
            {
                _worker.Join(2500);
            }

            _stopEvent.Dispose();
            _refreshEvent.Dispose();
        }
    }
}
