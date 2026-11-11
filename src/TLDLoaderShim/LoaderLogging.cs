using System;
using System.IO;
using System.Linq;

namespace TLDLoader
{
    /// A UseLogger mod's own log: Logs/<ID>/<ID>_<yyyy-MM-dd>.log next to the mods, the newest five kept, each line
    /// "<time> [<level>] <message>", and (EchoToGameLog) the same line in the game's log through the host.
    public class Logger : ILogger
    {
        public enum LogLevel { Debug, Info, Warning, Error, Critical }

        private const int KeepFiles = 5;
        private static readonly object FileLock = new object();
        private readonly string _id, _dir;
        private readonly bool _echo;
        private string _day, _file;

        public Logger(Mod mod)
        {
            _id = mod.ID;
            _echo = mod.EchoToGameLog;
            _dir = ModLoader.GetModLogFolder(mod);
            Directory.CreateDirectory(_dir);
            foreach (var old in Directory.GetFiles(_dir, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Skip(KeepFiles))
                try { File.Delete(old); } catch { }
            Write(mod.Name + " v" + mod.Version + " started at " + DateTime.Now.ToString("s") + ".");
        }

        private void Write(string line)
        {
            string day = DateTime.Now.ToString("yyyy-MM-dd");
            if (day != _day) { _day = day; _file = Path.Combine(_dir, _id + "_" + day + ".log"); }
            lock (FileLock)
            {
                try { File.AppendAllText(_file, line + "\r\n"); } catch { }
            }
        }

        public void Log(string msg, LogLevel level = LogLevel.Info)
        {
            string line = DateTime.Now.ToString("s") + " [" + level + "] " + msg;
            Write(line);
            if (_echo) ModLoader.Echo?.Invoke("[" + _id + "] " + line, level == LogLevel.Warning ? 1 : level >= LogLevel.Error ? 2 : 0);
        }

        public void LogDebug(string msg) => Log(msg, LogLevel.Debug);
        public void LogInfo(string msg) => Log(msg, LogLevel.Info);
        public void LogWarning(string msg) => Log(msg, LogLevel.Warning);
        public void LogError(string msg) => Log(msg, LogLevel.Error);
        public void LogCritical(string msg) => Log(msg, LogLevel.Critical);
    }
}
