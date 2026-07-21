using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexQuotaTray
{
    internal static class UserSettings
    {
        private const string SettingsKey = @"Software\CodexQuotaTray";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "CodexQuotaTray";

        public static bool PopupPinned
        {
            get { return ReadBoolean("PopupPinned", false); }
            set { WriteBoolean("PopupPinned", value); }
        }

        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    object value = key == null ? null : key.GetValue(RunValueName);
                    if (value == null)
                    {
                        return false;
                    }

                    string command = Convert.ToString(value);
                    string expected = "\"" + Application.ExecutablePath + "\"";
                    return command.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetAutoStart(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("无法打开 Windows 启动项注册表。");
                }

                if (enabled)
                {
                    string command = "\"" + Application.ExecutablePath + "\" --autostart";
                    key.SetValue(RunValueName, command, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(RunValueName, false);
                }
            }
        }

        private static bool ReadBoolean(string name, bool defaultValue)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey, false))
                {
                    object value = key == null ? null : key.GetValue(name);
                    if (value == null)
                    {
                        return defaultValue;
                    }

                    return Convert.ToInt32(value) != 0;
                }
            }
            catch
            {
                return defaultValue;
            }
        }

        private static void WriteBoolean(string name, bool value)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key != null)
                    {
                        key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
