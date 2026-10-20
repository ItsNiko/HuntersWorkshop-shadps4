// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Management;
using System.Runtime.InteropServices;

namespace HuntersWorkshop
{
    public static class ProcessLaunch
    {
        [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);

        public static string[] Parse(string commandLine)
        {
            int count;
            IntPtr memory = CommandLineToArgvW(commandLine, out count);
            if (memory == IntPtr.Zero) throw new InvalidOperationException("Cannot read the game's launch arguments.");
            try
            {
                var args = new string[count];
                for (int i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory, i * IntPtr.Size));
                return args;
            }
            finally { LocalFree(memory); }
        }

        public static string UserDirectory(int processId, string port)
        {
            try
            {
                using (var query = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + processId))
                using (var rows = query.Get())
                {
                    foreach (ManagementObject row in rows)
                    {
                        using (row)
                        {
                            string text = row["CommandLine"] as string;
                            if (String.IsNullOrWhiteSpace(text)) return null;
                            string[] args = Parse(text);
                            for (int i = 1; i + 1 < args.Length; i++)
                            {
                                if (args[i] != "--user") continue;
                                return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(args[i + 1])
                                    ? args[i + 1] : System.IO.Path.Combine(port, args[i + 1]));
                            }
                        }
                    }
                }
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            return null;
        }
    }
}
