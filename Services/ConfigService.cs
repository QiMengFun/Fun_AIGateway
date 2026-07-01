using Newtonsoft.Json;
using FunAiGateway.Models;

namespace FunAiGateway.Services
{
    public class ConfigService
    {
        private static readonly string ConfigDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");

        private AppConfig _config = new();
        private readonly object _lock = new();

        // 日志服务实例：负责文件日志的写入、读取、裁剪、清理等工作
        // 在构造函数末尾创建，持有对 ConfigService 的引用以读取日志相关配置
        public LogService LogService { get; }

        public AppConfig Config => _config;

        public event Action? ConfigChanged;

        public ConfigService()
        {
            Load();
            MigrateLegacyConfig();
            // 在配置加载完成后创建日志服务，避免循环依赖
            LogService = new LogService(this);
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

        // 迁移旧配置：将旧的单 BaseUrl/ApiKey 按 Type 迁移到对应协议端点
        private void MigrateLegacyConfig()
        {
            lock (_lock)
            {
                bool changed = false;
                foreach (var channel in _config.Channels)
                {
                    // 旧配置有 BaseUrl 但新字段为空，迁移
                    if (!string.IsNullOrEmpty(channel.BaseUrl))
                    {
                        if (channel.Type == ChannelType.Anthropic)
                        {
                            if (string.IsNullOrEmpty(channel.AnthropicBaseUrl))
                            {
                                channel.AnthropicBaseUrl = channel.BaseUrl;
                                channel.AnthropicApiKey = channel.ApiKey;
                                changed = true;
                            }
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(channel.OpenAIBaseUrl))
                            {
                                channel.OpenAIBaseUrl = channel.BaseUrl;
                                channel.OpenAIApiKey = channel.ApiKey;
                                changed = true;
                            }
                        }
                    }
                }
                if (changed) { Save(); }
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

        // 查找渠道中可用的协议端点（用于自动识别请求应走哪个协议链路）
        // 返回该渠道配置中已填写的可用协议端点（OpenAI/Anthropic 可同时存在）
        public static ChannelProtocolEndpoints GetChannelEndpoints(ChannelConfig channel)
        {
            return new ChannelProtocolEndpoints
            {
                HasOpenAI = !string.IsNullOrWhiteSpace(channel.OpenAIBaseUrl),
                HasAnthropic = !string.IsNullOrWhiteSpace(channel.AnthropicBaseUrl)
            };
        }

        // 渠道可用协议端点信息
        public class ChannelProtocolEndpoints
        {
            public bool HasOpenAI { get; set; }
            public bool HasAnthropic { get; set; }
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

        // 获取所有启用的模型配置（含上下文长度、最大输出等详细信息）
        public List<ModelConfig> GetAllModelConfigs()
        {
            lock (_lock)
            {
                return _config.Channels
                    .Where(c => c.Enabled)
                    .SelectMany(c => c.Models.Where(m => m.Enabled))
                    .GroupBy(m => m.ModelName)
                    .Select(g => g.First())
                    .OrderBy(m => m.ModelName)
                    .ToList();
            }
        }

        // ========= API Key 管理 =========

        // 添加 API Key
        public void AddApiKey(ApiKeyConfig apiKey)
        {
            lock (_lock)
            {
                _config.ApiKeys.Add(apiKey);
                Save();
            }
        }

        // 更新 API Key
        public void UpdateApiKey(ApiKeyConfig apiKey)
        {
            lock (_lock)
            {
                var idx = _config.ApiKeys.FindIndex(k => k.Id == apiKey.Id);
                if (idx >= 0)
                {
                    _config.ApiKeys[idx] = apiKey;
                    Save();
                }
            }
        }

        // 删除 API Key
        public void DeleteApiKey(string keyId)
        {
            lock (_lock)
            {
                _config.ApiKeys.RemoveAll(k => k.Id == keyId);
                Save();
            }
        }

        // 根据 Key 值查找启用的 ApiKeyConfig（用于请求鉴权）
        public ApiKeyConfig? FindApiKey(string keyValue)
        {
            if (string.IsNullOrEmpty(keyValue)) return null;
            lock (_lock)
            {
                return _config.ApiKeys.FirstOrDefault(k => k.Enabled && k.Key == keyValue);
            }
        }

        // 判断指定 Key 是否允许访问指定模型
        public bool IsKeyAllowedModel(ApiKeyConfig apiKey, string modelName)
        {
            if (apiKey == null) return false;
            // 空列表表示允许访问全部模型
            if (apiKey.AllowedModels == null || apiKey.AllowedModels.Count == 0) return true;
            return apiKey.AllowedModels.Contains(modelName, StringComparer.OrdinalIgnoreCase);
        }

        // 判断指定 Key 是否已过期（ExpiresAt=null 表示永不过期）
        // 返回 true 表示已过期，false 表示未过期或无限制
        public bool IsKeyExpired(ApiKeyConfig apiKey)
        {
            if (apiKey == null) return true;
            if (!apiKey.ExpiresAt.HasValue) return false;
            return DateTime.Now > apiKey.ExpiresAt.Value;
        }

        // 扣减 Key 的剩余调用次数（RemainingCalls=0 表示不限，不扣减）
        // 返回 true 表示允许调用，false 表示次数已耗尽
        // 仅检查不扣减，实际扣减在请求成功后由 DecrementKeyCalls 执行
        public bool HasRemainingCalls(string keyId)
        {
            lock (_lock)
            {
                var k = _config.ApiKeys.FirstOrDefault(x => x.Id == keyId);
                if (k == null) return false;
                // 0 表示不限制
                if (k.RemainingCalls == 0) return true;
                return k.RemainingCalls > 0;
            }
        }

        // 实际扣减 Key 的剩余调用次数（请求成功后调用）
        public bool DecrementKeyCalls(string keyId)
        {
            lock (_lock)
            {
                var k = _config.ApiKeys.FirstOrDefault(x => x.Id == keyId);
                if (k == null) return false;
                // 0 表示不限制，不扣减
                if (k.RemainingCalls == 0) return true;
                if (k.RemainingCalls <= 0) return false;
                k.RemainingCalls--;
                Save();
                return true;
            }
        }

        // ===================== 以下为日志相关委托方法 =====================
        // 实际逻辑已迁移至 LogService（见 Services/LogService.cs）
        // 这里保留与原签名一致的委托方法，保证调用方（如 MainForm、ProxyServer）无需大幅改动

        // 添加请求日志到文件
        public void AddLog(RequestLog log) => LogService.AddLog(log);

        // 裁剪日志到指定条数（删除最早的记录）
        public void TrimLogs(int maxCount) => LogService.TrimLogs(maxCount);

        // 获取最近的若干条请求日志（按时间正序，旧的在前）
        public List<RequestLog> GetRecentLogs(int count = 100) => LogService.GetRecentLogs(count);

        // 清空所有请求日志文件
        public void ClearLogs() => LogService.ClearLogs();

        // 将上游响应内容写入文件，用于排查限流、错误等问题
        public void WriteResponseLog(string channelName, int statusCode, string modelName, string responseBody)
            => LogService.WriteResponseLog(channelName, statusCode, modelName, responseBody);

        // 清空所有响应日志文件
        public void ClearResponseLogs() => LogService.ClearResponseLogs();
    }
}
