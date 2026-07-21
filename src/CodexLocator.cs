using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace CodexQuotaTray
{
    internal static class CodexLocator
    {
        public static string FindExecutable()
        {
            List<string> candidates = new List<string>();
            AddCandidate(candidates, Environment.GetEnvironmentVariable("CODEX_QUOTA_CODEX_PATH"));
            AddCandidate(candidates, Environment.GetEnvironmentVariable("CODEX_PATH"));

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!String.IsNullOrEmpty(localAppData))
            {
                string desktopBin = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
                if (Directory.Exists(desktopBin))
                {
                    try
                    {
                        IEnumerable<string> desktopCopies = Directory
                            .EnumerateFiles(desktopBin, "codex.exe", SearchOption.AllDirectories)
                            .OrderByDescending(delegate(string path)
                            {
                                try { return File.GetLastWriteTimeUtc(path); }
                                catch { return DateTime.MinValue; }
                            });
                        foreach (string path in desktopCopies)
                        {
                            AddCandidate(candidates, path);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            try
            {
                foreach (Process process in Process.GetProcessesByName("codex"))
                {
                    try
                    {
                        AddCandidate(candidates, process.MainModule.FileName);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
            }

            string pathValue = Environment.GetEnvironmentVariable("PATH");
            if (!String.IsNullOrEmpty(pathValue))
            {
                foreach (string directory in pathValue.Split(Path.PathSeparator))
                {
                    string trimmed = directory.Trim().Trim('"');
                    if (!String.IsNullOrEmpty(trimmed))
                    {
                        AddCandidate(candidates, Path.Combine(trimmed, "codex.exe"));
                    }
                }
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!String.IsNullOrEmpty(userProfile))
            {
                AddCandidate(candidates, Path.Combine(userProfile, ".local", "bin", "codex.exe"));
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        public static string ResolveCodexHome()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!String.IsNullOrEmpty(configured))
            {
                return Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (String.IsNullOrEmpty(userProfile))
            {
                userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            }

            return String.IsNullOrEmpty(userProfile) ? null : Path.Combine(userProfile, ".codex");
        }

        private static void AddCandidate(ICollection<string> candidates, string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (!candidates.Contains(expanded, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(expanded);
            }
        }
    }
}
