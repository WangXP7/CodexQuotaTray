using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexQuotaTray
{
    internal static class SessionLogQuotaProvider
    {
        private const int TailBytes = 512 * 1024;
        private const int MaximumFiles = 12;

        public static QuotaSnapshot ReadLatest()
        {
            string codexHome = CodexLocator.ResolveCodexHome();
            if (String.IsNullOrEmpty(codexHome))
            {
                return null;
            }

            string sessionsRoot = Path.Combine(codexHome, "sessions");
            if (!Directory.Exists(sessionsRoot))
            {
                return null;
            }

            List<FileInfo> files;
            try
            {
                files = Directory.EnumerateFiles(sessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                    .Select(delegate(string path) { return new FileInfo(path); })
                    .OrderByDescending(delegate(FileInfo file) { return file.LastWriteTimeUtc; })
                    .Take(MaximumFiles)
                    .ToList();
            }
            catch
            {
                return null;
            }

            QuotaSnapshot newest = null;
            foreach (FileInfo file in files)
            {
                QuotaSnapshot candidate = ReadLatestFromFile(file);
                if (candidate != null &&
                    (newest == null || candidate.ObservedAtUtc > newest.ObservedAtUtc))
                {
                    newest = candidate;
                }
            }

            return newest;
        }

        private static QuotaSnapshot ReadLatestFromFile(FileInfo file)
        {
            string text;
            try
            {
                using (FileStream stream = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, stream.Length - TailBytes);
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

                    text = Encoding.UTF8.GetString(bytes, 0, offset);
                    if (start > 0)
                    {
                        int firstNewline = text.IndexOf('\n');
                        text = firstNewline >= 0 ? text.Substring(firstNewline + 1) : String.Empty;
                    }
                }
            }
            catch
            {
                return null;
            }

            string[] lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = TailBytes;
            for (int index = lines.Length - 1; index >= 0; index--)
            {
                string line = lines[index].TrimEnd('\r');
                if (line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0 ||
                    line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                try
                {
                    IDictionary<string, object> root = serializer.DeserializeObject(line) as IDictionary<string, object>;
                    QuotaSnapshot snapshot = QuotaJsonParser.ParseSessionEvent(root, file.LastWriteTimeUtc);
                    if (snapshot != null)
                    {
                        return snapshot;
                    }
                }
                catch
                {
                    // The active JSONL file may end in a partially-written line.
                }
            }

            return null;
        }
    }
}
