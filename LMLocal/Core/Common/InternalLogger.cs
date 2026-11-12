using System;
using System.Diagnostics;
using System.IO;

namespace LMLocal.Core.Common
{
    internal interface IInternalLogger
    {
        void Debug(string message);
        void Info(string message);
        void Warn(string message);
        void Error(string message, Exception ex = null);
    }

    internal class DummyLogger : IInternalLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception ex = null) { }
    }

    /// <summary>
    /// Writes diagnostics to a per-day file under %LOCALAPPDATA%\LMLocal\Logs.
    /// This logger is intentionally enabled in both Debug and Release VSIX builds.
    /// </summary>
    internal sealed class FileLogger : IInternalLogger
    {
        private readonly object _sync = new object();
        private readonly string _logDirectory;

        public FileLogger()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _logDirectory = Path.Combine(localAppData, "LMLocal", "Logs");
        }

        public void Debug(string message) => Write("DEBUG", message);
        public void Info(string message) => Write("INFO", message);
        public void Warn(string message) => Write("WARN", message);

        public void Error(string message, Exception ex = null)
        {
            string text = message ?? string.Empty;
            if (ex != null)
                text += Environment.NewLine + ex;
            Write("ERROR", text);
        }

        private void Write(string level, string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message ?? string.Empty}{Environment.NewLine}";
            try
            {
                lock (_sync)
                {
                    Directory.CreateDirectory(_logDirectory);
                    string path = Path.Combine(_logDirectory, "LMLocal-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    File.AppendAllText(path, line);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LMLocal logger] " + ex);
            }
        }
    }

    /// <summary>
    /// Central logger. File logging remains active in Release builds so installed VSIX
    /// packages can provide diagnostics without an attached debugger.
    /// </summary>
    internal static class InternalLogger
    {
        private static IInternalLogger _instance = new FileLogger();
        private static readonly object _lock = new object();

        public static string LogDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LMLocal", "Logs");
            }
        }

        public static void SetLogger(IInternalLogger logger)
        {
            lock (_lock)
                _instance = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static void Debug(string message) => SafeLog(l => l.Debug(message));
        public static void Info(string message) => SafeLog(l => l.Info(message));
        public static void Warn(string message) => SafeLog(l => l.Warn(message));
        public static void Error(string message, Exception ex = null) => SafeLog(l => l.Error(message, ex));

        private static void SafeLog(Action<IInternalLogger> action)
        {
            try
            {
                IInternalLogger logger;
                lock (_lock)
                    logger = _instance;
                action(logger);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[InternalLogger] " + ex);
            }
        }
    }
}