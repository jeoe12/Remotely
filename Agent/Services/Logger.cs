﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Remotely.Agent.Services
{
    public static class Logger
    {
        public static object WriteLock { get; } = new object();
        public static void Write(string message)
        {
            try
            {
                lock (WriteLock)
                {
                    var path = Path.Combine(Path.GetTempPath(), "Remotely_Logs.txt");
                    if (!File.Exists(path))
                    {
                        File.Create(path).Close();
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                        {
                            Process.Start("sudo", $"chmod 666 {path}").WaitForExit();
                        }
                    }
                    if (File.Exists(path))
                    {
                        var fi = new FileInfo(path);
                        while (fi.Length > 1000000)
                        {
                            var content = File.ReadAllLines(path);
                            File.WriteAllLines(path, content.Skip(10));
                            fi = new FileInfo(path);
                        }
                    }
                    File.AppendAllText(path, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")}\t[INFO]\t{message}{Environment.NewLine}");
                    Console.WriteLine(message);
                }
            }
            catch { }
        }

        public static void Write(Exception ex)
        {
            lock (WriteLock)
            {
                try
                {
                    var exception = ex;
                    var path = Path.Combine(Path.GetTempPath(), "Remotely_Logs.txt");

                    if (!File.Exists(path))
                    {
                        File.Create(path).Close();
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                        {
                            Process.Start("sudo", $"chmod 666 {path}").WaitForExit();
                        }
                    }

                    while (exception != null)
                    {
                        var jsonError = new
                        {
                            Type = "Error",
                            Timestamp = DateTime.Now.ToString(),
                            Message = exception?.Message,
                            Source = exception?.Source,
                            StackTrace = exception?.StackTrace,
                        };
                        if (File.Exists(path))
                        {
                            var fi = new FileInfo(path);
                            while (fi.Length > 1000000)
                            {
                                var content = File.ReadAllLines(path);
                                File.WriteAllLines(path, content.Skip(10));
                                fi = new FileInfo(path);
                            }
                        }
                        File.AppendAllText(path, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")}\t[ERROR]\t{exception?.Message}\t{exception?.StackTrace}\t{exception?.Source}{Environment.NewLine}");
                        Console.WriteLine(exception.Message);
                        exception = exception.InnerException;
                    }
                }
                catch { }
            }
        }
    }
}
