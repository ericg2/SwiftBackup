using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace SwiftUtils
{
    public enum LogType { DEBUG, INFO, WARNING, ERROR, FATAL }


    /// <summary>
    /// This class allows a user to easily log information to a file or console output. It uses multi-threading
    /// to prevent blocking I/O operations.
    /// </summary>
    public class SimpleLogger : IDisposable
    {

        private static List<SimpleLogger> _RunningLogs = new List<SimpleLogger>();
        private static Thread? _LogThread = null;
        private static bool _LogThreadRunning = false;

        private static void HandleLogCache()
        {
            while (_LogThreadRunning)
            {
                foreach (SimpleLogger logger in _RunningLogs)
                {
                    if (!logger.IsRunning || logger.LogCache.Count == 0 || logger.LogStream == null)
                        continue;

                    while (logger.LogCache.TryDequeue(out string? res))
                    {
                        bool bad = false;
                        for (int i = 0; i < 3; i++)
                        {
                            try
                            {
                                logger.LogStream.Write(Encoding.UTF8.GetBytes(res + Environment.NewLine));
                                logger.LogStream.Flush();
                                bad = false;
                                break;
                            }
                            catch (Exception)
                            {
                                bad = true;
                            }
                        }
                        if (bad)
                        {
                            // Too many errors have occurred. Close the connection and prevent the usrer from continuing.
                            logger.Stop();
                            break;
                        }
                    }
                }
            }
        }
 
        public string Name { private set; get; }
        public string DirectoryPath { private set; get; }
        public bool IsRunning { private set; get; }

        public ConcurrentQueue<string> LogCache { private set; get; }
        public FileStream? LogStream { private set; get; }

        public string LogPath
        {
            get
            {
                if (string.IsNullOrEmpty(Name) || string.IsNullOrEmpty(DirectoryPath))
                    return string.Empty;

                return $"{DirectoryPath}/{Name}_{DateTime.Now.ToString("yyyy-MM-dd-ss")}.log";
            }
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        public bool Stop()
        {
            // Remove the object from the list of registered Loggers.
            if (!IsRunning)
                return false;
            if (LogStream != null)
                LogStream.Close();

            _RunningLogs.Remove(this);
            IsRunning = false;

            if (_RunningLogs.Count == 0)
            {
                // Shut off the Thread running the queue.
                _LogThreadRunning = false;
                _LogThread = null;
            }
            return true;
        }

        public bool Start()
        {
            // Attempt to open the directory as a file handle.
            if (IsRunning)
                return false;

            try
            {
                if (LogStream == null)
                    LogStream = File.Open(LogPath, FileMode.Append, FileAccess.Write);
            }
            catch (Exception)
            {
                return false;
            }

            _RunningLogs.Add(this);
            IsRunning = true;

            // Make sure the manager thread is running.
            if (_RunningLogs.Count > 0 && _LogThread == null)
            {
                try
                {
                    _LogThread = new Thread(new ThreadStart(HandleLogCache));
                    _LogThreadRunning = true;
                    _LogThread.Start();
                }
                catch (Exception)
                {
                    _LogThreadRunning = false;
                    _LogThread = null;
                    return false;
                }
            }
            return true;
        }

        [MemberNotNull(nameof(DirectoryPath))]
        public bool SetDirectory(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath) || File.Exists(directoryPath))
            {
                DirectoryPath = string.Empty;
                return false;
            }
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    // The path does not exist -- attempt to create it
                    Directory.CreateDirectory(directoryPath);
                }
                if (IsRunning)
                {
                    if (LogStream != null)
                        LogStream.Close();
                    LogStream = File.Open(LogPath, FileMode.Append, FileAccess.Write);
                    DirectoryPath = directoryPath;
                    return LogStream != null;
                }
                else
                {
                    DirectoryPath = directoryPath;
                    return true; // do not open the file unless we are running.
                }
            }
            catch (Exception)
            {
                DirectoryPath = string.Empty;
                return false;
            }
        }

        public bool Log(LogType type, string message)
        {
            if (!IsRunning)
                return false;
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            LogCache.Enqueue($"{timestamp} [{type}] {message}");    
            return true;
        }

        public bool Debug(string message) { return Log(LogType.DEBUG, message); }
        public bool Info(string message) { return Log(LogType.INFO, message); }
        public bool Warn(string message) { return Log(LogType.WARNING, message); }
        public bool Error(string message) { return Log(LogType.ERROR, message); }
        public bool Fatal(string message) { return Log(LogType.FATAL, message); }

        public SimpleLogger([CallerMemberName] string name = "", string directoryPath = "logs") 
        {
            Name = name;
            if (!SetDirectory(directoryPath))
                DirectoryPath = string.Empty;

            IsRunning = false;
            LogCache = new ConcurrentQueue<string>();
            LogStream = null;
        }
    }
}
