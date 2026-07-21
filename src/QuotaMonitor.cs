using System;
using System.IO;
using System.Threading;

namespace CodexQuotaTray
{
    internal sealed class QuotaMonitor : IDisposable
    {
        private readonly CodexRateLimitClient _client = new CodexRateLimitClient();
        private readonly object _stateLock = new object();
        private Timer _periodicTimer;
        private Timer _debounceTimer;
        private FileSystemWatcher _watcher;
        private DateTime _lastServerSnapshotUtc = DateTime.MinValue;
        private DateTime _lastServerObservedUtc = DateTime.MinValue;
        private QuotaSnapshot _lastFallback;
        private int _scanRunning;
        private bool _disposed;

        public event Action<QuotaSnapshot> SnapshotChanged;
        public event Action<string> StatusChanged;

        public void Start()
        {
            _client.SnapshotReceived += OnServerSnapshot;
            _client.StatusChanged += OnStatusChanged;
            _client.Start();

            _debounceTimer = new Timer(delegate { ScanFallback(true); }, null, Timeout.Infinite, Timeout.Infinite);
            _periodicTimer = new Timer(delegate { ScanFallback(false); }, null, 1500, 15000);
            StartWatcher();
        }

        public void RefreshNow()
        {
            _client.RequestNow();
            QueueFallbackScan(0);
        }

        private void StartWatcher()
        {
            string codexHome = CodexLocator.ResolveCodexHome();
            if (String.IsNullOrEmpty(codexHome))
            {
                return;
            }

            string sessions = Path.Combine(codexHome, "sessions");
            if (!Directory.Exists(sessions))
            {
                return;
            }

            try
            {
                _watcher = new FileSystemWatcher(sessions, "*.jsonl");
                _watcher.IncludeSubdirectories = true;
                _watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                _watcher.Changed += OnSessionFileChanged;
                _watcher.Created += OnSessionFileChanged;
                _watcher.Renamed += OnSessionFileRenamed;
                _watcher.EnableRaisingEvents = true;
            }
            catch
            {
                if (_watcher != null)
                {
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
        }

        private void OnSessionFileChanged(object sender, FileSystemEventArgs args)
        {
            QueueFallbackScan(700);
        }

        private void OnSessionFileRenamed(object sender, RenamedEventArgs args)
        {
            QueueFallbackScan(700);
        }

        private void QueueFallbackScan(int delayMilliseconds)
        {
            Timer timer = _debounceTimer;
            if (timer == null)
            {
                return;
            }

            try { timer.Change(delayMilliseconds, Timeout.Infinite); }
            catch (ObjectDisposedException) { }
        }

        private void ScanFallback(bool fileWasChanged)
        {
            if (_disposed || Interlocked.Exchange(ref _scanRunning, 1) != 0)
            {
                return;
            }

            try
            {
                bool serverIsFresh;
                DateTime serverObservedUtc;
                lock (_stateLock)
                {
                    serverIsFresh = DateTime.UtcNow - _lastServerSnapshotUtc < TimeSpan.FromMinutes(2);
                    serverObservedUtc = _lastServerObservedUtc;
                }

                if (serverIsFresh && !fileWasChanged)
                {
                    return;
                }

                QuotaSnapshot snapshot = SessionLogQuotaProvider.ReadLatest();
                if (snapshot == null)
                {
                    if (_lastServerSnapshotUtc == DateTime.MinValue)
                    {
                        EmitStatus("等待 Codex 产生额度数据…");
                    }

                    return;
                }

                lock (_stateLock)
                {
                    _lastFallback = snapshot;
                    serverIsFresh = DateTime.UtcNow - _lastServerSnapshotUtc < TimeSpan.FromMinutes(2);
                    serverObservedUtc = _lastServerObservedUtc;
                }

                if (serverObservedUtc == DateTime.MinValue || snapshot.ObservedAtUtc > serverObservedUtc)
                {
                    EmitSnapshot(snapshot);
                    EmitStatus(snapshot.IsOlderThan(TimeSpan.FromMinutes(10))
                        ? "正在显示较旧的本地缓存"
                        : "正在显示 Codex 本地缓存");
                }
                else if (!serverIsFresh)
                {
                    EmitStatus("实时接口暂不可用，保留最近额度");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _scanRunning, 0);
            }
        }

        private void OnServerSnapshot(QuotaSnapshot snapshot)
        {
            lock (_stateLock)
            {
                _lastServerSnapshotUtc = DateTime.UtcNow;
                _lastServerObservedUtc = snapshot.ObservedAtUtc;
            }

            EmitSnapshot(snapshot);
        }

        private void OnStatusChanged(string status)
        {
            EmitStatus(status);
        }

        private void EmitSnapshot(QuotaSnapshot snapshot)
        {
            Action<QuotaSnapshot> handler = SnapshotChanged;
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
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }

            if (_periodicTimer != null)
            {
                _periodicTimer.Dispose();
            }

            if (_debounceTimer != null)
            {
                _debounceTimer.Dispose();
            }

            _client.Dispose();
        }
    }
}
