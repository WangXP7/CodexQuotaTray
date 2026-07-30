using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexQuotaTray
{
    internal sealed class CodexTaskInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public int EstimatedProgressPercent { get; set; }
        public TimeSpan EstimatedRemaining { get; set; }
    }

    internal sealed class CodexTaskMonitor : IDisposable
    {
        private const int PollMilliseconds = 5000;
        private const int MaximumFiles = 32;
        private const int TailBytes = 8 * 1024 * 1024;
        private static readonly TimeSpan ActiveFileAge = TimeSpan.FromMinutes(30);

        private readonly Dictionary<string, CachedLog> _cache =
            new Dictionary<string, CachedLog>(StringComparer.OrdinalIgnoreCase);
        private Timer _timer;
        private int _scanRunning;
        private volatile bool _disposed;

        public event Action<IList<CodexTaskInfo>> TasksChanged;

        public void Start()
        {
            if (_disposed || _timer != null)
            {
                return;
            }

            _timer = new Timer(Scan, null, 0, PollMilliseconds);
        }

        private void Scan(object state)
        {
            if (_disposed || Interlocked.Exchange(ref _scanRunning, 1) != 0)
            {
                return;
            }

            try
            {
                IList<CodexTaskInfo> tasks = ReadCurrentTasks();
                Action<IList<CodexTaskInfo>> handler = TasksChanged;
                if (handler != null)
                {
                    handler(tasks);
                }
            }
            catch
            {
                // Task display is supplementary and must not affect quota monitoring.
            }
            finally
            {
                Interlocked.Exchange(ref _scanRunning, 0);
            }
        }

        private IList<CodexTaskInfo> ReadCurrentTasks()
        {
            DateTime now = DateTime.UtcNow;
            List<FileInfo> files = FindRecentSessionFiles();
            HashSet<string> visiblePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (FileInfo file in files)
            {
                visiblePaths.Add(file.FullName);
                CachedLog cached;
                bool changed = !_cache.TryGetValue(file.FullName, out cached) ||
                    cached.Length != file.Length ||
                    cached.LastWriteTimeUtc != file.LastWriteTimeUtc;

                if (changed)
                {
                    ParseResult parsed = ParseLog(file, cached == null ? 0 : cached.Length);
                    if (cached == null)
                    {
                        cached = new CachedLog();
                    }

                    if (parsed.SawLifecycle)
                    {
                        cached.ActiveTask = parsed.ActiveTask;
                    }
                    else if (cached.ActiveTask != null && parsed.LastActivityUtc > cached.ActiveTask.UpdatedAtUtc)
                    {
                        cached.ActiveTask.Detail = parsed.LastActivityDetail;
                        cached.ActiveTask.UpdatedAtUtc = parsed.LastActivityUtc;
                    }

                    cached.Length = file.Length;
                    cached.LastWriteTimeUtc = file.LastWriteTimeUtc;
                    _cache[file.FullName] = cached;
                }
            }

            foreach (string path in _cache.Keys.ToList())
            {
                CachedLog cached = _cache[path];
                if (!visiblePaths.Contains(path) ||
                    now - cached.LastWriteTimeUtc > ActiveFileAge)
                {
                    _cache.Remove(path);
                }
            }

            List<CodexTaskInfo> tasks = new List<CodexTaskInfo>();
            foreach (CachedLog cached in _cache.Values)
            {
                if (cached.ActiveTask == null || now - cached.LastWriteTimeUtc > ActiveFileAge)
                {
                    continue;
                }

                CodexTaskInfo task = Clone(cached.ActiveTask);
                ApplyEstimate(task, now);
                tasks.Add(task);
            }

            return tasks
                .GroupBy(delegate(CodexTaskInfo task) { return task.Id; })
                .Select(delegate(IGrouping<string, CodexTaskInfo> group)
                {
                    return group.OrderByDescending(delegate(CodexTaskInfo task) { return task.UpdatedAtUtc; }).First();
                })
                .OrderBy(delegate(CodexTaskInfo task) { return task.StartedAtUtc; })
                .ToList();
        }

        private List<FileInfo> FindRecentSessionFiles()
        {
            string codexHome = CodexLocator.ResolveCodexHome();
            if (String.IsNullOrEmpty(codexHome))
            {
                return new List<FileInfo>();
            }

            string sessionsRoot = Path.Combine(codexHome, "sessions");
            if (!Directory.Exists(sessionsRoot))
            {
                return new List<FileInfo>();
            }

            try
            {
                DateTime cutoff = DateTime.UtcNow - ActiveFileAge;
                return Directory
                    .EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                    .Select(delegate(string path) { return new FileInfo(path); })
                    .Where(delegate(FileInfo file) { return file.LastWriteTimeUtc >= cutoff; })
                    .OrderByDescending(delegate(FileInfo file) { return file.LastWriteTimeUtc; })
                    .Take(MaximumFiles)
                    .ToList();
            }
            catch
            {
                return new List<FileInfo>();
            }
        }

        private static ParseResult ParseLog(FileInfo file, long previousLength)
        {
            ParseResult result = new ParseResult();
            string text = ReadSegment(file, previousLength);
            if (String.IsNullOrEmpty(text))
            {
                return result;
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = TailBytes * 2;
            using (StringReader reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!IsRelevant(line))
                    {
                        continue;
                    }

                    try
                    {
                        IDictionary<string, object> root =
                            serializer.DeserializeObject(line) as IDictionary<string, object>;
                        IDictionary<string, object> payload =
                            GetValue(root, "payload") as IDictionary<string, object>;
                        string type = GetString(payload, "type");
                        DateTime eventTime = ParseEventTime(root, file.LastWriteTimeUtc);

                        if (String.Equals(type, "task_started", StringComparison.Ordinal))
                        {
                            string turnId = GetString(payload, "turn_id");
                            result.ActiveTask = new CodexTaskInfo
                            {
                                Id = String.IsNullOrEmpty(turnId) ? file.Name : turnId,
                                Name = String.Empty,
                                Detail = "任务已开始，正在准备执行",
                                StartedAtUtc = ParseUnixTime(GetValue(payload, "started_at"), eventTime),
                                UpdatedAtUtc = eventTime
                            };
                            result.SawLifecycle = true;
                        }
                        else if (String.Equals(type, "task_complete", StringComparison.Ordinal))
                        {
                            string turnId = GetString(payload, "turn_id");
                            if (result.ActiveTask == null ||
                                String.IsNullOrEmpty(turnId) ||
                                String.Equals(result.ActiveTask.Id, turnId, StringComparison.Ordinal))
                            {
                                result.ActiveTask = null;
                            }

                            result.SawLifecycle = true;
                        }
                        else if (String.Equals(type, "user_message", StringComparison.Ordinal))
                        {
                            if (result.ActiveTask != null && String.IsNullOrEmpty(result.ActiveTask.Name))
                            {
                                result.ActiveTask.Name = CleanText(GetString(payload, "message"), 72);
                                result.ActiveTask.UpdatedAtUtc = eventTime;
                            }
                        }
                        else if (String.Equals(type, "agent_message", StringComparison.Ordinal) &&
                            String.Equals(GetString(payload, "phase"), "commentary", StringComparison.Ordinal))
                        {
                            UpdateActivity(result, CleanText(GetString(payload, "message"), 110), eventTime);
                        }
                        else if (String.Equals(type, "custom_tool_call", StringComparison.Ordinal) ||
                            String.Equals(type, "function_call", StringComparison.Ordinal))
                        {
                            string input = GetString(payload, "input");
                            if (String.IsNullOrEmpty(input))
                            {
                                input = GetString(payload, "arguments");
                            }

                            UpdateActivity(
                                result,
                                DescribeActivity(
                                    GetString(payload, "name"),
                                    input),
                                eventTime);
                        }
                        else if (String.Equals(type, "patch_apply_end", StringComparison.Ordinal))
                        {
                            UpdateActivity(result, "正在修改程序文件", eventTime);
                        }
                    }
                    catch
                    {
                        // The active JSONL file may contain a partially-written final line.
                    }
                }
            }

            if (result.ActiveTask != null)
            {
                if (String.IsNullOrEmpty(result.ActiveTask.Name))
                {
                    result.ActiveTask.Name = "Codex 并行任务";
                }

                if (String.IsNullOrEmpty(result.ActiveTask.Detail))
                {
                    result.ActiveTask.Detail = "正在执行";
                }
            }

            return result;
        }

        private static string ReadSegment(FileInfo file, long previousLength)
        {
            try
            {
                using (FileStream stream = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = previousLength > 0 && previousLength <= stream.Length
                        ? Math.Max(0, previousLength - (64 * 1024))
                        : Math.Max(0, stream.Length - TailBytes);
                    if (stream.Length - start > TailBytes)
                    {
                        start = stream.Length - TailBytes;
                    }
                    stream.Seek(start, SeekOrigin.Begin);
                    int byteCount = (int)Math.Min(TailBytes, stream.Length - start);
                    byte[] bytes = new byte[byteCount];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0)
                        {
                            break;
                        }

                        offset += read;
                    }

                    string text = Encoding.UTF8.GetString(bytes, 0, offset);
                    if (start > 0)
                    {
                        int firstNewline = text.IndexOf('\n');
                        text = firstNewline >= 0 ? text.Substring(firstNewline + 1) : String.Empty;
                    }

                    return text;
                }
            }
            catch
            {
                return String.Empty;
            }
        }

        private static bool IsRelevant(string line)
        {
            return line.IndexOf("\"task_started\"", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("\"task_complete\"", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("\"user_message\"", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("\"agent_message\"", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("\"custom_tool_call\"", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("\"function_call\"", StringComparison.Ordinal) >= 0 ||
                line.IndexOf("\"patch_apply_end\"", StringComparison.Ordinal) >= 0;
        }

        private static void UpdateActivity(ParseResult result, string detail, DateTime eventTime)
        {
            if (String.IsNullOrEmpty(detail))
            {
                return;
            }

            result.LastActivityDetail = detail;
            result.LastActivityUtc = eventTime;
            if (result.ActiveTask != null)
            {
                result.ActiveTask.Detail = detail;
                result.ActiveTask.UpdatedAtUtc = eventTime;
            }
        }

        private static string DescribeActivity(string name, string input)
        {
            string toolName = name ?? String.Empty;
            string combined = (name ?? String.Empty) + " " + (input ?? String.Empty);
            if (combined.IndexOf("apply_patch", StringComparison.OrdinalIgnoreCase) >= 0)
                return "正在修改程序文件";
            if (combined.IndexOf("test.ps1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                toolName.IndexOf("test", StringComparison.OrdinalIgnoreCase) >= 0)
                return "正在运行测试";
            if (combined.IndexOf("build.ps1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("build", StringComparison.OrdinalIgnoreCase) >= 0)
                return "正在编译程序";
            if (combined.IndexOf("git", StringComparison.OrdinalIgnoreCase) >= 0)
                return "正在执行 Git 操作";
            if (combined.IndexOf("view_image", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("screenshot", StringComparison.OrdinalIgnoreCase) >= 0)
                return "正在检查界面预览";
            if (combined.IndexOf("web", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0)
                return "正在查询资料";
            if (combined.IndexOf("Get-Content", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("rg ", StringComparison.OrdinalIgnoreCase) >= 0)
                return "正在检查代码和数据";
            if (String.Equals(toolName, "wait", StringComparison.OrdinalIgnoreCase))
                return "正在等待后台操作完成";
            if (toolName.IndexOf("load_workspace", StringComparison.OrdinalIgnoreCase) >= 0)
                return "正在加载工作环境";
            if (toolName.IndexOf("update_plan", StringComparison.OrdinalIgnoreCase) >= 0)
                return "正在更新执行计划";
            if (toolName.IndexOf("request_user_input", StringComparison.OrdinalIgnoreCase) >= 0)
                return "正在等待用户输入";
            if (combined.IndexOf("shell_command", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("exec_command", StringComparison.OrdinalIgnoreCase) >= 0)
                return "正在执行系统命令";
            return "正在执行工具操作";
        }

        private static void ApplyEstimate(CodexTaskInfo task, DateTime now)
        {
            double elapsedSeconds = Math.Max(1, (now - task.StartedAtUtc).TotalSeconds);
            double estimatedTotalSeconds = Math.Max(600, elapsedSeconds * 1.15);
            int progress = (int)Math.Round(elapsedSeconds / estimatedTotalSeconds * 100);
            task.EstimatedProgressPercent = Math.Max(3, Math.Min(92, progress));
            task.EstimatedRemaining = TimeSpan.FromSeconds(
                Math.Max(15, estimatedTotalSeconds - elapsedSeconds));
        }

        private static CodexTaskInfo Clone(CodexTaskInfo task)
        {
            return new CodexTaskInfo
            {
                Id = task.Id,
                Name = task.Name,
                Detail = task.Detail,
                StartedAtUtc = task.StartedAtUtc,
                UpdatedAtUtc = task.UpdatedAtUtc
            };
        }

        private static object GetValue(IDictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) ? value : null;
        }

        private static string GetString(IDictionary<string, object> dictionary, string key)
        {
            object value = GetValue(dictionary, key);
            return value == null ? String.Empty : Convert.ToString(value);
        }

        private static DateTime ParseEventTime(IDictionary<string, object> root, DateTime fallback)
        {
            DateTime parsed;
            string value = GetString(root, "timestamp");
            return DateTime.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out parsed)
                ? parsed
                : fallback;
        }

        private static DateTime ParseUnixTime(object value, DateTime fallback)
        {
            try
            {
                long seconds = Convert.ToInt64(value);
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
            }
            catch
            {
                return fallback;
            }
        }

        private static string CleanText(string value, int maximumLength)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return String.Empty;
            }

            string result = value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Replace("`", String.Empty)
                .Trim();
            while (result.IndexOf("  ", StringComparison.Ordinal) >= 0)
            {
                result = result.Replace("  ", " ");
            }

            return result.Length <= maximumLength
                ? result
                : result.Substring(0, maximumLength - 1) + "…";
        }

        public void Dispose()
        {
            _disposed = true;
            Timer timer = _timer;
            _timer = null;
            if (timer != null)
            {
                timer.Dispose();
            }
        }

        private sealed class CachedLog
        {
            public long Length;
            public DateTime LastWriteTimeUtc;
            public CodexTaskInfo ActiveTask;
        }

        private sealed class ParseResult
        {
            public bool SawLifecycle;
            public CodexTaskInfo ActiveTask;
            public string LastActivityDetail;
            public DateTime LastActivityUtc;
        }
    }
}
