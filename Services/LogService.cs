using Newtonsoft.Json;
using FunAiGateway.Models;

namespace FunAiGateway.Services
{
    /// <summary>
    /// 日志服务：负责请求日志与响应内容日志的写入、读取、裁剪、清理等工作。
    /// 从 ConfigService 与 ProxyServer 中拆分而来，集中管理文件日志相关逻辑。
    /// 通过持有 ConfigService 引用读取日志相关配置（MaxLogCount、EnableResponseLog 等）。
    /// </summary>
    public class LogService
    {
        // 日志目录：exe目录下的 logs 文件夹
        private static readonly string LogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        // 请求日志目录：exe目录下的 logs/requests 文件夹
        private static readonly string RequestLogDir = Path.Combine(LogDir, "requests");
        // 响应内容日志目录：exe目录下的 logs/responses 文件夹
        private static readonly string ResponseLogDir = Path.Combine(LogDir, "responses");
        // 单个日志文件最大大小（50MB），超过则分割
        private const long MaxLogSizeBytes = 50 * 1024 * 1024;

        private readonly ConfigService _configService;
        // 日志文件操作专用锁，避免并发写入冲突
        private readonly object _logLock = new();
        // 日志条数内存计数，避免每次写日志都扫描文件
        private int _logLineCount = -1; // -1 表示尚未初始化

        public LogService(ConfigService configService)
        {
            _configService = configService;
        }

        // 请求日志
        // 获取当前日志文件名：按天分割，当天文件超过50M则加序号
        private string GetCurrentLogFile()
        {
            var dateStr = DateTime.Now.ToString("yyyy-MM-dd");
            var baseFile = Path.Combine(RequestLogDir, $"requests_{dateStr}.log");

            // 检查文件大小，超过50M则递增序号
            var file = baseFile;
            var seq = 1;
            while (File.Exists(file) && new FileInfo(file).Length >= MaxLogSizeBytes)
            {
                file = Path.Combine(RequestLogDir, $"requests_{dateStr}_{seq}.log");
                seq++;
            }
            return file;
        }

        // 获取指定日期范围内的所有日志文件（按日期排序）
        private List<string> GetLogFiles()
        {
            if (!Directory.Exists(RequestLogDir)) return new();
            return Directory.GetFiles(RequestLogDir, "requests_*.log")
                .OrderByDescending(f => f) // 文件名含日期，按名称倒序=最新的在前
                .ToList();
        }

        public void AddLog(RequestLog log)
        {
            lock (_logLock)
            {
                try
                {
                    Directory.CreateDirectory(RequestLogDir);
                    var line = JsonConvert.SerializeObject(log, Formatting.None);
                    var file = GetCurrentLogFile();
                    File.AppendAllText(file, line + Environment.NewLine);

                    // 按保留天数清理过期的请求日志文件
                    CleanExpiredRequestLogs();

                    // 超过上限则自动裁剪最早的记录
                    var maxCount = _configService.Config.MaxLogCount;
                    if (maxCount > 0)
                    {
                        // 懒初始化行数计数
                        if (_logLineCount < 0)
                            _logLineCount = CountAllLogLines();
                        else
                            _logLineCount++;

                        if (_logLineCount > maxCount)
                        {
                            TrimLogs(maxCount);
                            _logLineCount = CountAllLogLines();
                        }
                    }
                }
                catch { }
            }
        }

        // 统计所有日志文件的总行数
        private int CountAllLogLines()
        {
            try
            {
                var files = GetLogFiles();
                int count = 0;
                foreach (var file in files)
                {
                    count += File.ReadAllLines(file).Count(l => !string.IsNullOrWhiteSpace(l));
                }
                return count;
            }
            catch { return 0; }
        }

        // 裁剪日志到指定条数（删除最早的记录）
        public void TrimLogs(int maxCount)
        {
            if (maxCount <= 0) return;

            lock (_logLock)
            {
                try
                {
                    var files = GetLogFiles(); // 按名称倒序（最新的在前）
                    if (files.Count == 0) return;

                    // 读取所有文件的非空行并统计
                    int totalCount = 0;
                    var fileLines = new Dictionary<string, List<string>>();
                    foreach (var file in files)
                    {
                        var lines = File.ReadAllLines(file)
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .ToList();
                        fileLines[file] = lines;
                        totalCount += lines.Count;
                    }

                    if (totalCount <= maxCount) return;

                    // 需要移除的条数
                    int toRemove = totalCount - maxCount;

                    // 从最旧的文件开始移除（files 是最新的在前，所以从末尾遍历）
                    for (int i = files.Count - 1; i >= 0 && toRemove > 0; i--)
                    {
                        var file = files[i];
                        var lines = fileLines[file];

                        if (toRemove >= lines.Count)
                        {
                            // 整个文件都可删除
                            File.Delete(file);
                            toRemove -= lines.Count;
                        }
                        else
                        {
                            // 保留该文件中最新的 (lines.Count - toRemove) 行
                            var keepLines = lines.Skip(toRemove).ToList();
                            File.WriteAllLines(file, keepLines);
                            toRemove = 0;
                        }
                    }

                    // 更新内存计数
                    _logLineCount = maxCount;
                }
                catch { }
            }
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
            lock (_logLock)
            {
                try
                {
                    if (!Directory.Exists(RequestLogDir)) return;
                    foreach (var file in GetLogFiles())
                        File.Delete(file);
                    _logLineCount = 0;
                }
                catch { }
            }
        }

        // 响应内容日志
        // 将上游响应内容写入文件，用于排查限流、错误等问题
        // 文件名格式：responses_2026-06-18_143025_渠道名_状态码.json
        // 自动清理超过 LogRetentionDays 天的响应日志文件
        public void WriteResponseLog(string channelName, int statusCode, string modelName, string responseBody)
        {
            try
            {
                Directory.CreateDirectory(ResponseLogDir);

                // 文件名中的渠道名去掉不合法字符
                var safeName = string.Join("_", channelName.Split(Path.GetInvalidFileNameChars()));
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                var fileName = $"responses_{timestamp}_{safeName}_{statusCode}.json";
                var filePath = Path.Combine(ResponseLogDir, fileName);

                var entry = new
                {
                    time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    channel = channelName,
                    model = modelName,
                    statusCode,
                    response = responseBody
                };

                var json = JsonConvert.SerializeObject(entry, Formatting.Indented);
                File.WriteAllText(filePath, json);

                // 自动清理过期响应日志
                CleanExpiredResponseLogs();
            }
            catch { }
        }

        // 清理超过保留天数的响应日志文件
        private void CleanExpiredResponseLogs()
        {
            try
            {
                if (!Directory.Exists(ResponseLogDir)) { return; }
                var retentionDays = _configService.Config.LogRetentionDays;
                if (retentionDays <= 0) { return; }
                var cutoff = DateTime.Now.AddDays(-retentionDays);

                foreach (var file in Directory.GetFiles(ResponseLogDir, "responses_*.json"))
                {
                    if (File.GetCreationTime(file) < cutoff)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        // 清理超过保留天数的请求日志文件
        private void CleanExpiredRequestLogs()
        {
            try
            {
                if (!Directory.Exists(RequestLogDir)) { return; }
                var retentionDays = _configService.Config.LogRetentionDays;
                if (retentionDays <= 0) { return; }
                var cutoff = DateTime.Now.AddDays(-retentionDays);

                foreach (var file in Directory.GetFiles(RequestLogDir, "requests_*.log"))
                {
                    if (File.GetCreationTime(file) < cutoff)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch { }
        }

        // 清空所有响应日志
        public void ClearResponseLogs()
        {
            try
            {
                if (!Directory.Exists(ResponseLogDir)) { return; }
                foreach (var file in Directory.GetFiles(ResponseLogDir, "responses_*.json"))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }

        // 根据配置判断是否需要记录该状态码的响应内容
        public bool ShouldLogStatusCode(int statusCode)
        {
            var cfg = _configService.Config.ResponseLog;
            if (statusCode >= 200 && statusCode < 300) { return cfg.Log2xx; }
            if (statusCode >= 400 && statusCode < 500) { return cfg.Log4xx; }
            if (statusCode >= 500 && statusCode < 600) { return cfg.Log5xx; }
            return cfg.LogOther;
        }

        // 记录上游响应内容到文件（需开启 EnableResponseLog 且状态码匹配配置的记录范围）
        public void LogResponseContent(ChannelConfig channel, int statusCode, string modelName, string responseBody)
        {
            if (!_configService.Config.EnableResponseLog) { return; }
            if (!ShouldLogStatusCode(statusCode)) { return; }
            WriteResponseLog(channel.Name, statusCode, modelName, responseBody);
        }
    }
}