using Newtonsoft.Json;
using FunAiGateway.Models;

namespace FunAiGateway.Services
{
    public class ConfigService
    {
        private static readonly string ConfigDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");
        // 日志目录：exe目录下的 logs 文件夹
        private static readonly string LogDir = Path.Combine(ConfigDir, "logs");
        // 单个日志文件最大大小（50MB），超过则分割
        private const long MaxLogSizeBytes = 50 * 1024 * 1024;

        private AppConfig _config = new();
        private readonly object _lock = new();

        public AppConfig Config => _config;

        public event Action? ConfigChanged;

        public ConfigService()
        {
            Load();
        }

        public void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(ConfigFile))
                    {
                        var json = File.ReadAllText(ConfigFile);
                        _config = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
                    }
                }
                catch
                {
                    _config = new AppConfig();
                }
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(ConfigDir);
                    var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                    File.WriteAllText(ConfigFile, json);
                    ConfigChanged?.Invoke();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ConfigService.Save error: {ex.Message}");
                }
            }
        }

        // 渠道管理
        public void AddChannel(ChannelConfig channel)
        {
            lock (_lock)
            {
                _config.Channels.Add(channel);
                Save();
            }
        }

        public void UpdateChannel(ChannelConfig channel)
        {
            lock (_lock)
            {
                var idx = _config.Channels.FindIndex(c => c.Id == channel.Id);
                if (idx >= 0)
                {
                    _config.Channels[idx] = channel;
                    Save();
                }
            }
        }

        public void DeleteChannel(string channelId)
        {
            lock (_lock)
            {
                _config.Channels.RemoveAll(c => c.Id == channelId);
                Save();
            }
        }

        // 查找模型对应的渠道
        public (ChannelConfig? channel, ModelConfig? model)? FindChannelForModel(string modelName)
        {
            lock (_lock)
            {
                var enabledChannels = _config.Channels
                    .Where(c => c.Enabled)
                    .ToList();

                foreach (var channel in enabledChannels)
                {
                    var model = channel.Models.FirstOrDefault(m =>
                        m.Enabled && m.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase));
                    if (model != null)
                        return (channel, model);
                }
                return null;
            }
        }

        // 获取所有可用模型名
        public List<string> GetAllModelNames()
        {
            lock (_lock)
            {
                var names = _config.Channels
                    .Where(c => c.Enabled)
                    .SelectMany(c => c.Models.Where(m => m.Enabled).Select(m => m.ModelName))
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
                return names;
            }
        }

        // 请求日志
        // 获取当前日志文件名：按天分割，当天文件超过50M则加序号
        private string GetCurrentLogFile()
        {
            var dateStr = DateTime.Now.ToString("yyyy-MM-dd");
            var baseFile = Path.Combine(LogDir, $"requests_{dateStr}.log");

            // 检查文件大小，超过50M则递增序号
            var file = baseFile;
            var seq = 1;
            while (File.Exists(file) && new FileInfo(file).Length >= MaxLogSizeBytes)
            {
                file = Path.Combine(LogDir, $"requests_{dateStr}_{seq}.log");
                seq++;
            }
            return file;
        }

        // 获取指定日期范围内的所有日志文件（按日期排序）
        private List<string> GetLogFiles()
        {
            if (!Directory.Exists(LogDir)) return new();
            return Directory.GetFiles(LogDir, "requests_*.log")
                .OrderByDescending(f => f) // 文件名含日期，按名称倒序=最新的在前
                .ToList();
        }

        public void AddLog(RequestLog log)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                var line = JsonConvert.SerializeObject(log, Formatting.None);
                var file = GetCurrentLogFile();
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch { }
        }

        public List<RequestLog> GetRecentLogs(int count = 100)
        {
            try
            {
                var files = GetLogFiles();
                if (files.Count == 0) return new();

                var result = new List<RequestLog>();
                foreach (var file in files)
                {
                    if (result.Count >= count) break;
                    var lines = File.ReadAllLines(file)
                        .Where(l => !string.IsNullOrWhiteSpace(l));
                    foreach (var line in lines.Reverse())
                    {
                        var log = JsonConvert.DeserializeObject<RequestLog>(line);
                        if (log != null)
                            result.Add(log);
                        if (result.Count >= count) break;
                    }
                }
                // 结果需要按时间正序（旧的在前）
                result.Reverse();
                return result;
            }
            catch { return new(); }
        }

        public void ClearLogs()
        {
            try
            {
                if (!Directory.Exists(LogDir)) return;
                foreach (var file in GetLogFiles())
                    File.Delete(file);
            }
            catch { }
        }
    }
}
