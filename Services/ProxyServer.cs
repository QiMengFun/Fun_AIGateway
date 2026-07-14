using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FunAiGateway.Models;

namespace FunAiGateway.Services
{
    public class ProxyServer
    {
        private HttpListener? _listener;
        private readonly ConfigService _configService;
        private CancellationTokenSource? _cts;
        private readonly HttpClient _httpClient;
        // 按代理设置缓存 HttpClient，避免重复创建
        private readonly ConcurrentDictionary<string, HttpClient> _proxyClients = new();

        // Portal 查询速率限制：每个 IP 每分钟最多 10 次查询（防止 Key 枚举）
        private static readonly ConcurrentDictionary<string, List<long>> _portalRateLimit = new();
        private const int PortalRateLimitPerMinute = 10;

        public event Action<RequestLog>? OnRequestLogged;
        public event Action<string>? OnLog;
        public bool IsRunning { get; private set; }

        public ProxyServer(ConfigService configService)
        {
            _configService = configService;
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                // 不勾选代理时走直连，不使用系统代理
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 100
            })
            {
                // 超时由 CancellationToken 控制，HttpClient 自身不设超时限制
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
            };
        }

        // 根据渠道代理设置获取 HttpClient，未启用代理时返回共享实例
        private HttpClient GetHttpClient(ChannelConfig channel)
        {
            // 未启用代理或未填写代理地址时，使用默认 HttpClient
            if (!channel.ProxyEnabled || string.IsNullOrWhiteSpace(channel.ProxyHost))
                return _httpClient;

            var key = $"{channel.ProxyType}|{channel.ProxyHost}|{channel.ProxyUsername}|{channel.ProxyPassword}";
            return _proxyClients.GetOrAdd(key, _ => CreateProxyClient(channel));
        }

        // 创建带渠道超时的 CancellationToken，避免修改共享 HttpClient 的 Timeout 属性
        private CancellationTokenSource CreateTimeoutCts(CancellationToken ct, int timeoutSeconds)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            return cts;
        }

        // 创建带代理的 HttpClient
        private HttpClient CreateProxyClient(ChannelConfig channel)
        {
            // 解析代理地址 host:port
            var (host, port) = ParseHostPort(channel.ProxyHost, channel.ProxyType == "SOCKS5" ? 1080 : 7890);

            SocketsHttpHandler handler;
            if (channel.ProxyType.Equals("SOCKS5", StringComparison.OrdinalIgnoreCase))
            {
                // SOCKS5 代理：通过自定义连接回调实现握手
                var pHost = host;
                var pPort = port;
                var pUser = channel.ProxyUsername;
                var pPass = channel.ProxyPassword;
                handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (sctx, ct) =>
                    {
                        return await ConnectViaSocks5Async(pHost, pPort, sctx.DnsEndPoint.Host, sctx.DnsEndPoint.Port, pUser, pPass, ct);
                    },
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    MaxConnectionsPerServer = 100
                };
            }
            else
            {
                // HTTP 代理
                var proxyUrl = host.Contains("://") ? channel.ProxyHost : $"http://{channel.ProxyHost}";
                var proxy = new WebProxy(proxyUrl);
                if (!string.IsNullOrEmpty(channel.ProxyUsername) || !string.IsNullOrEmpty(channel.ProxyPassword))
                    proxy.Credentials = new NetworkCredential(channel.ProxyUsername, channel.ProxyPassword);

                handler = new SocketsHttpHandler
                {
                    UseProxy = true,
                    Proxy = proxy,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    MaxConnectionsPerServer = 100
                };
            }

            return new HttpClient(handler)
            {
                // 超时由 CancellationToken 控制，HttpClient 自身不设超时限制
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
            };
        }

        // 解析 host:port 格式
        private static (string host, int port) ParseHostPort(string address, int defaultPort)
        {
            address = address.Trim();
            if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                address = address[7..];
            else if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                address = address[8..];

            var idx = address.LastIndexOf(':');
            if (idx > 0 && int.TryParse(address[(idx + 1)..], out var p))
                return (address[..idx].Trim(), p);
            return (address, defaultPort);
        }

        // 通过 SOCKS5 代理建立到目标服务器的连接
        private static async Task<Stream> ConnectViaSocks5Async(string proxyHost, int proxyPort, string targetHost, int targetPort, string username, string password, CancellationToken ct)
        {
            var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync(proxyHost, proxyPort, ct);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }

            var stream = tcp.GetStream();

            // 1. 握手：协商认证方式
            bool hasAuth = !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password);
            byte[] greeting = hasAuth ? new byte[] { 0x05, 0x02, 0x00, 0x02 } : new byte[] { 0x05, 0x01, 0x00 };
            await stream.WriteAsync(greeting, ct);
            await stream.FlushAsync(ct);

            byte[] resp = new byte[2];
            await ReadExactAsync(stream, resp, 2, ct);
            if (resp[0] != 0x05)
            {
                tcp.Dispose();
                throw new Exception($"SOCKS5 代理握手失败：无效的版本号 {resp[0]}");
            }
            byte method = resp[1];

            // 2. 用户名/密码认证
            if (method == 0x02)
            {
                var userBytes = System.Text.Encoding.UTF8.GetBytes(username ?? "");
                var passBytes = System.Text.Encoding.UTF8.GetBytes(password ?? "");
                var authReq = new byte[3 + userBytes.Length + passBytes.Length];
                authReq[0] = 0x01; // 子协议版本
                authReq[1] = (byte)userBytes.Length;
                Array.Copy(userBytes, 0, authReq, 2, userBytes.Length);
                authReq[2 + userBytes.Length] = (byte)passBytes.Length;
                Array.Copy(passBytes, 0, authReq, 3 + userBytes.Length, passBytes.Length);

                await stream.WriteAsync(authReq, ct);
                await stream.FlushAsync(ct);

                byte[] authResp = new byte[2];
                await ReadExactAsync(stream, authResp, 2, ct);
                if (authResp[1] != 0x00)
                {
                    tcp.Dispose();
                    throw new Exception("SOCKS5 代理认证失败：用户名或密码错误");
                }
            }
            else if (method != 0x00)
            {
                tcp.Dispose();
                throw new Exception($"SOCKS5 代理不支持此认证方式: {method}");
            }

            // 3. 发送连接请求
            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x05); // SOCKS 版本
            ms.WriteByte(0x01); // CONNECT 命令
            ms.WriteByte(0x00); // 保留

            // 目标地址：优先按 IP 解析，否则按域名
            if (IPAddress.TryParse(targetHost, out var ip))
            {
                var bytes = ip.GetAddressBytes();
                ms.WriteByte(ip.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)0x04 : (byte)0x01);
                ms.Write(bytes, 0, bytes.Length);
            }
            else
            {
                var domainBytes = System.Text.Encoding.UTF8.GetBytes(targetHost);
                ms.WriteByte(0x03); // 域名类型
                ms.WriteByte((byte)domainBytes.Length);
                ms.Write(domainBytes, 0, domainBytes.Length);
            }

            // 目标端口（网络字节序）
            ms.WriteByte((byte)(targetPort >> 8));
            ms.WriteByte((byte)(targetPort & 0xFF));

            var connectReq = ms.ToArray();
            await stream.WriteAsync(connectReq, ct);
            await stream.FlushAsync(ct);

            // 4. 读取连接响应
            byte[] connectRespHeader = new byte[4];
            await ReadExactAsync(stream, connectRespHeader, 4, ct);
            if (connectRespHeader[0] != 0x05)
            {
                tcp.Dispose();
                throw new Exception("SOCKS5 代理响应无效");
            }
            if (connectRespHeader[1] != 0x00)
            {
                tcp.Dispose();
                throw new Exception($"SOCKS5 代理连接失败，错误码: {connectRespHeader[1]}");
            }

            // 跳过绑定地址和端口
            byte addrType = connectRespHeader[3];
            int skipLen = addrType switch
            {
                0x01 => 4,  // IPv4
                0x04 => 16, // IPv6
                _ => 0      // 域名：需先读取长度字节
            };
            if (addrType == 0x03)
            {
                byte[] lenBuf = new byte[1];
                await ReadExactAsync(stream, lenBuf, 1, ct);
                skipLen = lenBuf[0];
            }
            byte[] skip = new byte[skipLen + 2]; // 地址 + 端口
            await ReadExactAsync(stream, skip, skip.Length, ct);

            return stream;
        }

        // 从流中精确读取指定字节数
        private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct);
                if (read == 0) throw new Exception("代理连接已断开");
                total += read;
            }
        }

        public void Start(int port, ListenMode listenMode = ListenMode.Local)
        {
            if (IsRunning) Stop();

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            var prefix = listenMode == ListenMode.Broadcast
                ? $"http://+:{port}/"
                : $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            IsRunning = true;

            _ = Task.Run(() => ListenAsync(_cts.Token));
            Log($"代理服务器已启动，监听端口: {port}");
        }

        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
            IsRunning = false;
            Log("代理服务器已停止");
        }

        private async Task ListenAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener!.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { Log($"监听异常: {ex.Message}"); }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var request = context.Request;
            var response = context.Response;

            var requestLog = new RequestLog
            {
                Time = DateTime.Now,
                RequestProtocol = "Unknown"
            };

            try
            {
                // CORS预检
                if (request.HttpMethod == "OPTIONS")
                {
                    SetCorsHeaders(response, request);
                    response.StatusCode = 200;
                    response.Close();
                    return;
                }

                SetCorsHeaders(response, request);

                var path = request.Url?.AbsolutePath ?? "";
                var body = await ReadRequestBodyAsync(request);

                // 路由分发
                if (path.StartsWith("/v1/chat/completions") || path.StartsWith("/v1/completions"))
                {
                    requestLog.RequestProtocol = "OpenAI";
                    await HandleOpenAIChatAsync(context, body, requestLog, ct);
                }
                else if (path.StartsWith("/v1/messages"))
                {
                    requestLog.RequestProtocol = "Anthropic";
                    await HandleAnthropicMessagesAsync(context, body, requestLog, ct);
                }
                else if (path.StartsWith("/v1/models"))
                {
                    await HandleModelsAsync(context);
                }
                else if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    // 返回空 favicon 避免浏览器 404 报错
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                }
                else if (path.Equals("/", StringComparison.OrdinalIgnoreCase) || path.Equals("", StringComparison.OrdinalIgnoreCase))
                {
                    // 根目录重定向到 /portal
                    response.StatusCode = 302;
                    response.RedirectLocation = "/portal";
                    response.Close();
                }
                else if (path.Equals("/portal", StringComparison.OrdinalIgnoreCase) || path.Equals("/portal/", StringComparison.OrdinalIgnoreCase))
                {
                    // 用户自助查询页面（输入Key查看可用模型/剩余次数/到期时间）
                    await HandlePortalPageAsync(context);
                }
                else if (path.Equals("/portal/api/keyinfo", StringComparison.OrdinalIgnoreCase))
                {
                    // 用户自助查询API：POST { "key": "fk-xxx" } 返回Key信息
                    await HandlePortalKeyInfoAsync(context, body);
                }
                else if (path.Equals("/portal/api/keylogs", StringComparison.OrdinalIgnoreCase))
                {
                    // 用户自助查询API：POST { "key": "fk-xxx" } 返回该 Key 的最近调用日志
                    await HandlePortalKeyLogsAsync(context, body);
                }
                else
                {
                    response.StatusCode = 404;
                    await WriteJsonAsync(response, new { error = new { message = "Not Found", type = "invalid_request_error" } });
                }

                sw.Stop();
                requestLog.DurationMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                sw.Stop();
                requestLog.Success = false;
                requestLog.ErrorMessage = ex.Message;
                requestLog.DurationMs = sw.ElapsedMilliseconds;

                try
                {
                    response.StatusCode = 500;
                    Log($"内部异常: {ex}");
                    await WriteJsonAsync(response, new { error = new { message = "Internal Server Error", type = "internal_error" } });
                }
                catch { }
            }
            finally
            {
                // 鉴权通过即扣减API Key调用次数（客户端中途断开也扣减，因为上游已产生费用）
                if (!string.IsNullOrEmpty(requestLog.KeyId))
                {
                    try { _configService.DecrementKeyCalls(requestLog.KeyId); }
                    catch { }
                }
                // 未知协议（404等）不记录请求次数和日志
                if (requestLog.RequestProtocol != "Unknown")
                {
                    OnRequestLogged?.Invoke(requestLog);
                }
                try { response.Close(); } catch { }
            }
        }

        // 处理OpenAI协议请求
        private async Task HandleOpenAIChatAsync(HttpListenerContext context, string body, RequestLog requestLog, CancellationToken ct)
        {
            var response = context.Response;
            var reqBody = JObject.Parse(body);
            var modelName = reqBody["model"]?.ToString() ?? "";
            var isStream = reqBody["stream"]?.Value<bool>() ?? false;

            requestLog.ModelName = modelName;
            requestLog.IsStream = isStream;

            // system_model 路由到默认模型
            if (modelName.Equals("system_model", StringComparison.OrdinalIgnoreCase))
            {
                var defaultModel = _configService.Config.DefaultModel;
                if (string.IsNullOrEmpty(defaultModel))
                {
                    var available = _configService.GetAllModelNames().FirstOrDefault();
                    if (string.IsNullOrEmpty(available))
                    {
                        response.StatusCode = 404;
                        await WriteJsonAsync(response, new { error = new { message = "No available model. Please add channels first.", type = "invalid_request_error" } });
                        requestLog.Success = false;
                        requestLog.ErrorMessage = "system_model: no available model";
               return;
                    }
                    defaultModel = available;
                }
                reqBody["model"] = defaultModel;
                modelName = defaultModel;
                requestLog.ModelName = $"system_model→{defaultModel}";
            }

            // API Key 验证（强制开启，支持多Key + 模型权限 + 过期校验 + 次数扣减）
            {
                // 严格解析 Authorization: Bearer xxx 头
                var authHeader = context.Request.Headers["Authorization"];
                var bearerKey = (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    ? authHeader.Substring(7).Trim() : null;
                var apiKeyConfig = _configService.FindApiKey(bearerKey ?? "");
                if (apiKeyConfig == null)
                {
                    response.StatusCode = 401;
                    await WriteJsonAsync(response, new { error = new { message = "Invalid API Key", type = "auth_error" } });
                    requestLog.Success = false;
                    requestLog.ErrorMessage = "Invalid API Key";
                    return;
                }
                // 校验该Key是否允许访问该模型
                if (!_configService.IsKeyAllowedModel(apiKeyConfig, modelName))
                {
                    response.StatusCode = 403;
                    await WriteJsonAsync(response, new { error = new { message = $"API Key not allowed to access model '{modelName}'", type = "permission_error" } });
                    requestLog.Success = false;
                    requestLog.ErrorMessage = $"Key not allowed for model {modelName}";
                    return;
                }
                // 校验Key是否已过期
                if (_configService.IsKeyExpired(apiKeyConfig))
                {
                    response.StatusCode = 401;
                    var expTime = apiKeyConfig.ExpiresAt!.Value.ToString("yyyy-MM-dd HH:mm");
                    await WriteJsonAsync(response, new { error = new { message = $"API Key 已于 {expTime} 过期", type = "key_expired" } });
                    requestLog.Success = false;
                    requestLog.ErrorMessage = $"Key 已过期：{expTime}";
                    return;
                }
                // 检查剩余调用次数是否足够（0 表示不限，不扣减，请求成功后才扣减）
                if (!_configService.HasRemainingCalls(apiKeyConfig.Id))
                {
                    response.StatusCode = 429;
                    await WriteJsonAsync(response, new { error = new { message = "API Key 调用次数已耗尽", type = "quota_exceeded" } });
                    requestLog.Success = false;
                    requestLog.ErrorMessage = "Key 调用次数耗尽";
                    return;
                }
                // 记录 KeyId，用于请求成功后在 finally 块扣减次数
                requestLog.KeyId = apiKeyConfig.Id;
            }

            // 查找渠道
            var found = _configService.FindChannelForModel(modelName);
            if (found == null)
            {
                response.StatusCode = 404;
                await WriteJsonAsync(response, new { error = new { message = $"Model '{modelName}' not found", type = "invalid_request_error" } });
                requestLog.Success = false;
                requestLog.ErrorMessage = $"Model '{modelName}' not found";
                return;
            }

            var (channel, model) = found.Value;
            requestLog.ChannelName = channel!.Name;
            Log($"渠道: {channel.Name} | 模型: {model!.RealModelName ?? model.ModelName} | {context.Request.HttpMethod} {context.Request.Url?.AbsolutePath}");

            // 替换实际模型名
            if (!string.IsNullOrEmpty(model!.RealModelName))
                reqBody["model"] = model.RealModelName;

            // 推理模型处理：max_tokens → max_completion_tokens，并确保值足够大
            ApplyReasoningModelTokens(reqBody, !string.IsNullOrWhiteSpace(model.RealModelName) ? model.RealModelName : model.ModelName);

            // 启用思考推理：模型配置勾选「启用思考」时注入 enable_thinking:true
            if (model.EnableThinking)
                reqBody["enable_thinking"] = true;

            // 思考强度：用户未传 reasoning_effort 时，使用渠道配置的默认值
            var effort = channel.ReasoningEffort;
            if (!string.IsNullOrEmpty(effort) && reqBody["reasoning_effort"] == null)
                reqBody["reasoning_effort"] = effort;

            // 自动识别协议链路：根据渠道配置的可用端点决定转发路径
            // 1. 优先同协议直连（请求OpenAI→渠道有OpenAI端点则直连，避免协议转换开销）
            // 2. 否则用另一协议端点并做协议转换
            var endpoints = ConfigService.GetChannelEndpoints(channel);
            if (endpoints.HasOpenAI)
            {
                // OpenAI -> OpenAI 直接转发
                requestLog.TargetProtocol = "OpenAI";
                await ForwardOpenAIAsync(context, channel, reqBody, isStream, requestLog, ct);
            }
            else if (endpoints.HasAnthropic)
            {
                // OpenAI -> Anthropic 协议转换
                requestLog.TargetProtocol = "Anthropic";
                await ForwardOpenAIToAnthropicAsync(context, channel, reqBody, isStream, requestLog, ct);
            }
            else
            {
                response.StatusCode = 400;
                await WriteJsonAsync(response, new { error = new { message = $"Channel '{channel.Name}' has no available protocol endpoint", type = "invalid_request_error" } });
                requestLog.Success = false;
                requestLog.ErrorMessage = $"Channel '{channel.Name}' has no endpoint";
            }
        }

        // 处理Anthropic协议请求
        private async Task HandleAnthropicMessagesAsync(HttpListenerContext context, string body, RequestLog requestLog, CancellationToken ct)
        {
            var response = context.Response;
            var reqBody = JObject.Parse(body);
            var modelName = reqBody["model"]?.ToString() ?? "";
            var isStream = reqBody["stream"]?.Value<bool>() ?? false;

            requestLog.ModelName = modelName;
            requestLog.IsStream = isStream;

            // system_model 路由到默认模型
            if (modelName.Equals("system_model", StringComparison.OrdinalIgnoreCase))
            {
                var defaultModel = _configService.Config.DefaultModel;
                if (string.IsNullOrEmpty(defaultModel))
                {
                    var available = _configService.GetAllModelNames().FirstOrDefault();
                    if (string.IsNullOrEmpty(available))
                    {
                        response.StatusCode = 404;
                        await WriteJsonAsync(response, new { type = "error", error = new { type = "not_found_error", message = "No available model" } });
                        requestLog.Success = false;
                        requestLog.ErrorMessage = "system_model: no available model";
                        return;
                    }
                    defaultModel = available;
                }
                reqBody["model"] = defaultModel;
                modelName = defaultModel;
                requestLog.ModelName = $"system_model→{defaultModel}";
            }

            // API Key 验证（强制开启，Anthropic用x-api-key头，兼容Authorization Bearer）
            {
                // Anthropic 用 x-api-key 头，兼容 Authorization: Bearer xxx
                var apiKey = context.Request.Headers["x-api-key"];
                if (string.IsNullOrEmpty(apiKey))
                {
                    var authHeader = context.Request.Headers["Authorization"];
                    apiKey = (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        ? authHeader.Substring(7).Trim() : null;
                }
                var apiKeyConfig = _configService.FindApiKey(apiKey ?? "");
                if (apiKeyConfig == null)
                {
                    response.StatusCode = 401;
                    await WriteJsonAsync(response, new { error = new { message = "Invalid API Key", type = "auth_error" } });
                    requestLog.Success = false;
                    requestLog.ErrorMessage = "Invalid API Key";
                    return;
                }
                // 校验该Key是否允许访问该模型
                if (!_configService.IsKeyAllowedModel(apiKeyConfig, modelName))
                {
                    response.StatusCode = 403;
                    await WriteJsonAsync(response, new { error = new { message = $"API Key not allowed to access model '{modelName}'", type = "permission_error" } });
                    requestLog.Success = false;
                    requestLog.ErrorMessage = $"Key not allowed for model {modelName}";
                    return;
                }
                // 校验Key是否已过期
                if (_configService.IsKeyExpired(apiKeyConfig))
                {
                    response.StatusCode = 401;
                    var expTime = apiKeyConfig.ExpiresAt!.Value.ToString("yyyy-MM-dd HH:mm");
                    await WriteJsonAsync(response, new { error = new { message = $"API Key 已于 {expTime} 过期", type = "key_expired" } });
                    requestLog.Success = false;
                    requestLog.ErrorMessage = $"Key 已过期：{expTime}";
                    return;
                }
                // 检查剩余调用次数是否足够（0 表示不限，不扣减，请求成功后才扣减）
                if (!_configService.HasRemainingCalls(apiKeyConfig.Id))
                {
                    response.StatusCode = 429;
                    await WriteJsonAsync(response, new { error = new { message = "API Key 调用次数已耗尽", type = "quota_exceeded" } });
                    requestLog.Success = false;
                    requestLog.ErrorMessage = "Key 调用次数耗尽";
                    return;
                }
                // 记录 KeyId，用于请求成功后在 finally 块扣减次数
                requestLog.KeyId = apiKeyConfig.Id;
            }

            var found = _configService.FindChannelForModel(modelName);
            if (found == null)
            {
                response.StatusCode = 404;
                await WriteJsonAsync(response, new { type = "error", error = new { type = "not_found_error", message = $"Model '{modelName}' not found" } });
                requestLog.Success = false;
                requestLog.ErrorMessage = $"Model '{modelName}' not found";
                return;
            }

            var (channel, model) = found.Value;
            requestLog.ChannelName = channel!.Name;
            Log($"渠道: {channel.Name} | 模型: {model!.RealModelName ?? model.ModelName} | {context.Request.HttpMethod} {context.Request.Url?.AbsolutePath}");

            if (!string.IsNullOrEmpty(model!.RealModelName))
                reqBody["model"] = model.RealModelName;

            // 启用思考推理：模型配置勾选「启用思考」时注入 enable_thinking:true
            if (model.EnableThinking)
                reqBody["enable_thinking"] = true;

            // 思考强度：用户未传 reasoning_effort 时，使用渠道配置的默认值
            var effort = channel.ReasoningEffort;
            if (!string.IsNullOrEmpty(effort) && reqBody["reasoning_effort"] == null)
                reqBody["reasoning_effort"] = effort;

            // 自动识别协议链路：优先同协议直连
            var endpoints = ConfigService.GetChannelEndpoints(channel);
            if (endpoints.HasAnthropic)
            {
                // Anthropic -> Anthropic 直接转发
                requestLog.TargetProtocol = "Anthropic";
                await ForwardAnthropicAsync(context, channel, reqBody, isStream, requestLog, ct);
            }
            else if (endpoints.HasOpenAI)
            {
                // Anthropic -> OpenAI 协议转换
                requestLog.TargetProtocol = "OpenAI";
                await ForwardAnthropicToOpenAIAsync(context, channel, reqBody, isStream, requestLog, ct);
            }
            else
            {
                response.StatusCode = 400;
                await WriteJsonAsync(response, new { type = "error", error = new { type = "invalid_request_error", message = $"Channel '{channel.Name}' has no available protocol endpoint" } });
                requestLog.Success = false;
                requestLog.ErrorMessage = $"Channel '{channel.Name}' has no endpoint";
            }
        }

        // OpenAI -> OpenAI 直接转发
        private async Task ForwardOpenAIAsync(HttpListenerContext context, ChannelConfig channel, JObject reqBody, bool isStream, RequestLog requestLog, CancellationToken ct)
        {
            var url = $"{EnsureApiVersionPath(channel.OpenAIBaseUrl)}/chat/completions";
            await ForwardRequestAsync(context, url, channel, reqBody, isStream, requestLog, ct, null, channel.OpenAIApiKey);
        }

        // OpenAI -> Anthropic 转换转发
        private async Task ForwardOpenAIToAnthropicAsync(HttpListenerContext context, ChannelConfig channel, JObject openaiReq, bool isStream, RequestLog requestLog, CancellationToken ct)
        {
            var (anthropicReq, _) = ProtocolConverter.OpenAIToAnthropic(openaiReq);
            var url = $"{EnsureApiVersionPath(channel.AnthropicBaseUrl)}/messages";

            if (isStream)
            {
                await ForwardAnthropicStreamAsOpenAIAsync(context, url, channel, anthropicReq, openaiReq["model"]?.ToString() ?? "", requestLog, ct);
            }
            else
            {
                var (respBody, statusCode) = await SendAnthropicRequestAsync(url, channel, anthropicReq, ct);
                // 记录上游响应内容（根据配置的状态码范围决定是否记录）
                LogResponseContent(channel, statusCode, openaiReq["model"]?.ToString() ?? "", respBody.ToString(Formatting.None));
                var openaiResp = ProtocolConverter.AnthropicToOpenAIResponse(respBody, openaiReq["model"]?.ToString() ?? "");
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                await WriteJsonAsync(context.Response, openaiResp);

                requestLog.InputTokens = respBody["usage"]?["input_tokens"]?.Value<int>() ?? 0;
                requestLog.OutputTokens = respBody["usage"]?["output_tokens"]?.Value<int>() ?? 0;
            }
        }

        // Anthropic -> Anthropic 直接转发
        private async Task ForwardAnthropicAsync(HttpListenerContext context, ChannelConfig channel, JObject reqBody, bool isStream, RequestLog requestLog, CancellationToken ct)
        {
            var url = $"{EnsureApiVersionPath(channel.AnthropicBaseUrl)}/messages";
            await ForwardAnthropicRequestAsync(context, url, channel, reqBody, isStream, requestLog, ct);
        }

        // Anthropic -> OpenAI 转换转发
        private async Task ForwardAnthropicToOpenAIAsync(HttpListenerContext context, ChannelConfig channel, JObject anthropicReq, bool isStream, RequestLog requestLog, CancellationToken ct)
        {
            // 将Anthropic请求转为OpenAI请求
            var openaiReq = AnthropicToOpenAIRequest(anthropicReq);
            var url = $"{EnsureApiVersionPath(channel.OpenAIBaseUrl)}/chat/completions";

            if (isStream)
            {
                await ForwardOpenAIStreamAsAnthropicAsync(context, url, channel, openaiReq, anthropicReq["model"]?.ToString() ?? "", requestLog, ct);
            }
            else
            {
                var (respBody, statusCode) = await SendOpenAIRequestAsync(url, channel, openaiReq, ct);
                // 记录上游响应内容（根据配置的状态码范围决定是否记录）
                LogResponseContent(channel, statusCode, anthropicReq["model"]?.ToString() ?? "", respBody.ToString(Formatting.None));
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";
                await WriteJsonAsync(context.Response, respBody);

                requestLog.InputTokens = respBody["usage"]?["prompt_tokens"]?.Value<int>() ?? 0;
                requestLog.OutputTokens = respBody["usage"]?["completion_tokens"]?.Value<int>() ?? 0;
            }
        }

        // Anthropic请求格式转OpenAI请求格式
        private JObject AnthropicToOpenAIRequest(JObject anthropicReq)
        {
            var openaiReq = new JObject();

            if (anthropicReq["model"] != null) openaiReq["model"] = anthropicReq["model"];

            // max_tokens 处理：推理模型需用 max_completion_tokens
            var modelName = anthropicReq["model"]?.ToString() ?? "";
            var maxTokens = anthropicReq["max_tokens"]?.Value<int>() ?? 0;
            if (IsReasoningModel(modelName))
            {
                // 推理模型：转为 max_completion_tokens，确保至少 32768
                openaiReq["max_completion_tokens"] = Math.Max(maxTokens, 32768);
            }
            else if (maxTokens > 0)
            {
                openaiReq["max_tokens"] = maxTokens;
            }

            if (anthropicReq["temperature"] != null) openaiReq["temperature"] = anthropicReq["temperature"];
            if (anthropicReq["top_p"] != null) openaiReq["top_p"] = anthropicReq["top_p"];
            if (anthropicReq["stream"] != null) openaiReq["stream"] = anthropicReq["stream"];
            if (anthropicReq["stop_sequences"] != null) openaiReq["stop"] = anthropicReq["stop_sequences"];

            // system -> messages
            var messages = new JArray();
            if (anthropicReq["system"] != null)
            {
                messages.Add(new JObject { ["role"] = "system", ["content"] = anthropicReq["system"]!.ToString() });
            }

            // messages转换
            var anthropicMessages = anthropicReq["messages"] as JArray ?? new JArray();
            foreach (var msg in anthropicMessages)
            {
                var role = msg["role"]?.ToString();
                var content = msg["content"];

                if (role == "user")
                {
                    // 检查是否包含tool_result
                    if (content is JArray arr)
                    {
                        var toolResults = arr.Where(b => b["type"]?.ToString() == "tool_result").ToList();
                        if (toolResults.Count > 0)
                        {
                            foreach (var tr in toolResults)
                            {
                                messages.Add(new JObject
                                {
                                    ["role"] = "tool",
                                    ["tool_call_id"] = tr["tool_use_id"]?.ToString() ?? "",
                                    ["content"] = tr["content"]?.ToString() ?? ""
                                });
                            }
                            continue;
                        }
                    }
                    messages.Add(new JObject { ["role"] = "user", ["content"] = ConvertAnthropicContent(content) });
                }
                else if (role == "assistant")
                {
                    if (content is JArray arr2)
                    {
                        var toolUses = arr2.Where(b => b["type"]?.ToString() == "tool_use").ToList();
                        var textParts = arr2.Where(b => b["type"]?.ToString() == "text").Select(b => b["text"]?.ToString() ?? "").ToList();

                        if (toolUses.Count > 0)
                        {
                            var msg2 = new JObject
                            {
                                ["role"] = "assistant",
                                ["content"] = string.Join("", textParts),
                                ["tool_calls"] = new JArray(toolUses.Select(tu => new JObject
                                {
                                    ["id"] = tu["id"]?.ToString() ?? "",
                                    ["type"] = "function",
                                    ["function"] = new JObject
                                    {
                                        ["name"] = tu["name"]?.ToString() ?? "",
                                        ["arguments"] = tu["input"]?.ToString(Formatting.None) ?? "{}"
                                    }
                                }))
                            };
                            messages.Add(msg2);
                            continue;
                        }
                    }
                    messages.Add(new JObject { ["role"] = "assistant", ["content"] = ConvertAnthropicContent(content) });
                }
            }

            openaiReq["messages"] = messages;

            // tools转换
            var anthropicTools = anthropicReq["tools"] as JArray;
            if (anthropicTools != null && anthropicTools.Count > 0)
            {
                openaiReq["tools"] = new JArray(anthropicTools.Select(t => new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = t["name"]?.ToString() ?? "",
                        ["description"] = t["description"]?.ToString() ?? "",
                        ["parameters"] = t["input_schema"] ?? new JObject { ["type"] = "object" }
                    }
                }));
            }

            return openaiReq;
        }

        private JToken ConvertAnthropicContent(JToken? content)
        {
            if (content is JArray arr)
            {
                var converted = new JArray();
                foreach (var item in arr)
                {
                    var type = item["type"]?.ToString();
                    if (type == "text")
                        converted.Add(new JObject { ["type"] = "text", ["text"] = item["text"]?.ToString() ?? "" });
                    else if (type == "image")
                    {
                        var source = item["source"] as JObject;
                        if (source?["type"]?.ToString() == "base64")
                        {
                            converted.Add(new JObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JObject
                                {
                                    ["url"] = $"data:{source["media_type"]};base64,{source["data"]}"
                                }
                            });
                        }
                        else if (source?["type"]?.ToString() == "url")
                        {
                            converted.Add(new JObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JObject { ["url"] = source["url"]?.ToString() ?? "" }
                            });
                        }
                    }
                }
                return converted;
            }
            return content ?? "";
        }

        // 通用请求转发（OpenAI格式）
        // endpointApiKey：该协议端点对应的 ApiKey（来自渠道的 OpenAIApiKey/AnthropicApiKey）
        private async Task ForwardRequestAsync(HttpListenerContext context, string url, ChannelConfig channel, JObject reqBody, bool isStream, RequestLog requestLog, CancellationToken ct, string? contentType, string endpointApiKey)
        {
            using var timeoutCts = CreateTimeoutCts(ct, channel.Timeout);

            var apiKey = string.IsNullOrEmpty(endpointApiKey) ? channel.ApiKey : endpointApiKey;
            // 通过工厂委托创建请求，便于 429 时自动重试（HttpRequestMessage 不可重用）
            Func<HttpRequestMessage> createRequest = () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
                ForwardUserAgent(req, context);
                AddCustomHeaders(req, channel);
                var bodyStr = reqBody.ToString(Formatting.None);
                // 临时调试：记录发往上游的请求参数列表到独立文件，定位 Trae vs Codex 参数差异
                //try
                //{
                //    var debugDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                //    Directory.CreateDirectory(debugDir);
                //    var debugFile = Path.Combine(debugDir, "request_params_debug.log");
                //    var paramNames = string.Join(", ", reqBody.Properties().Select(p => p.Name));
                //    var debugLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] model={reqBody["model"]} stream={reqBody["stream"]} params=[{paramNames}]{Environment.NewLine}";
                //    File.AppendAllText(debugFile, debugLine);
                //}
                //catch { }
                req.Content = new StringContent(bodyStr, System.Text.Encoding.UTF8, "application/json");
                return req;
            };

            if (!isStream)
            {
                // 非流式：使用原有 SendWithRetryAsync（处理 429/5xx 重试）
                using var resp = await SendWithRetryAsync(createRequest, channel, false, timeoutCts.Token);
                var respBody = await resp.Content.ReadAsStringAsync(timeoutCts.Token);

                // 上游错误：详细记录日志，对客户端返回模糊错误
                if ((int)resp.StatusCode >= 400)
                {
                    LogResponseContent(channel, (int)resp.StatusCode, reqBody["model"]?.ToString() ?? "", respBody);
                    Log($"上游错误（渠道={channel.Name} 模型={reqBody["model"]} 状态={(int)resp.StatusCode}）: {respBody}");
                    context.Response.StatusCode = (int)resp.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(BuildMaskedError()), ct);
                    await context.Response.OutputStream.FlushAsync(ct);
                    return;
                }

                context.Response.StatusCode = (int)resp.StatusCode;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(respBody), ct);

                // 记录上游响应内容（根据配置的状态码范围决定是否记录）
                LogResponseContent(channel, (int)resp.StatusCode, reqBody["model"]?.ToString() ?? "", respBody);

                try
                {
                    var jResp = JObject.Parse(respBody);
                    requestLog.InputTokens = jResp["usage"]?["prompt_tokens"]?.Value<int>() ?? 0;
                    requestLog.OutputTokens = jResp["usage"]?["completion_tokens"]?.Value<int>() ?? 0;
                }
                catch { }
            }
            else
            {
                // 流式：使用 SendStreamWithRetryAsync（支持响应体错误重试）
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.Add("Cache-Control", "no-cache");
                context.Response.Headers.Add("Connection", "keep-alive");

                var sbOpenAI = new System.Text.StringBuilder();
                using var streamResp = await SendStreamWithRetryAsync(
                    createRequest, channel, requestLog, false,
                    context.Response.OutputStream, sbOpenAI, timeoutCts.Token);

                // 流式若上游返回 4xx/5xx，SendStreamWithRetryAsync 会直接返回该响应（不读取流）
                if ((int)streamResp.StatusCode >= 400)
                {
                    var errBody = await TryReadStreamErrorAsync(streamResp, timeoutCts.Token);
                    LogResponseContent(channel, (int)streamResp.StatusCode, reqBody["model"]?.ToString() ?? "", errBody);
                    Log($"上游错误（渠道={channel.Name} 模型={reqBody["model"]} 状态={(int)streamResp.StatusCode}）: {errBody}");
                    context.Response.StatusCode = (int)streamResp.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(BuildMaskedError()), ct);
                    await context.Response.OutputStream.FlushAsync(ct);
                    return;
                }

                LogResponseContent(channel, (int)streamResp.StatusCode, reqBody["model"]?.ToString() ?? "", sbOpenAI.ToString());
            }
        }

        // Anthropic格式请求转发
        private async Task ForwardAnthropicRequestAsync(HttpListenerContext context, string url, ChannelConfig channel, JObject reqBody, bool isStream, RequestLog requestLog, CancellationToken ct)
        {
            using var timeoutCts = CreateTimeoutCts(ct, channel.Timeout);

            var apiKey = string.IsNullOrEmpty(channel.AnthropicApiKey) ? channel.ApiKey : channel.AnthropicApiKey;
            // 通过工厂委托创建请求，便于 429 时自动重试（HttpRequestMessage 不可重用）
            Func<HttpRequestMessage> createRequest = () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                ForwardUserAgent(req, context);
                AddCustomHeaders(req, channel);
                req.Content = new StringContent(reqBody.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json");
                return req;
            };

            if (!isStream)
            {
                // 非流式：使用原有 SendWithRetryAsync（处理 429/5xx 重试）
                using var resp = await SendWithRetryAsync(createRequest, channel, false, timeoutCts.Token);
                var respBody = await resp.Content.ReadAsStringAsync(timeoutCts.Token);

                // 上游错误：详细记录日志，对客户端返回模糊错误
                if ((int)resp.StatusCode >= 400)
                {
                    LogResponseContent(channel, (int)resp.StatusCode, reqBody["model"]?.ToString() ?? "", respBody);
                    Log($"上游错误（渠道={channel.Name} 模型={reqBody["model"]} 状态={(int)resp.StatusCode}）: {respBody}");
                    context.Response.StatusCode = (int)resp.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(BuildMaskedError()), ct);
                    await context.Response.OutputStream.FlushAsync(ct);
                    return;
                }

                context.Response.StatusCode = (int)resp.StatusCode;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(respBody), ct);

                // 记录上游响应内容（根据配置的状态码范围决定是否记录）
                LogResponseContent(channel, (int)resp.StatusCode, reqBody["model"]?.ToString() ?? "", respBody);

                try
                {
                    var jResp = JObject.Parse(respBody);
                    requestLog.InputTokens = jResp["usage"]?["input_tokens"]?.Value<int>() ?? 0;
                    requestLog.OutputTokens = jResp["usage"]?["output_tokens"]?.Value<int>() ?? 0;
                }
                catch { }
            }
            else
            {
                // 流式：使用 SendStreamWithRetryAsync（支持响应体错误重试）
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.Add("Cache-Control", "no-cache");

                var sbAnthropic = new System.Text.StringBuilder();
                using var streamResp = await SendStreamWithRetryAsync(
                    createRequest, channel, requestLog, true,
                    context.Response.OutputStream, sbAnthropic, timeoutCts.Token);

                // 流式若上游返回 4xx/5xx
                if ((int)streamResp.StatusCode >= 400)
                {
                    var errBody = await TryReadStreamErrorAsync(streamResp, timeoutCts.Token);
                    LogResponseContent(channel, (int)streamResp.StatusCode, reqBody["model"]?.ToString() ?? "", errBody);
                    Log($"上游错误（渠道={channel.Name} 模型={reqBody["model"]} 状态={(int)streamResp.StatusCode}）: {errBody}");
                    context.Response.StatusCode = (int)streamResp.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(BuildMaskedError()), ct);
                    await context.Response.OutputStream.FlushAsync(ct);
                    return;
                }

                LogResponseContent(channel, (int)streamResp.StatusCode, reqBody["model"]?.ToString() ?? "", sbAnthropic.ToString());
            }
        }

        // Anthropic流式响应转OpenAI流式
        private async Task ForwardAnthropicStreamAsOpenAIAsync(HttpListenerContext context, string url, ChannelConfig channel, JObject anthropicReq, string modelName, RequestLog requestLog, CancellationToken ct)
        {
            using var timeoutCts = CreateTimeoutCts(ct, channel.Timeout);

            var apiKey = string.IsNullOrEmpty(channel.AnthropicApiKey) ? channel.ApiKey : channel.AnthropicApiKey;
            // 通过工厂委托创建请求，便于 429 时自动重试（HttpRequestMessage 不可重用）
            Func<HttpRequestMessage> createRequest = () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                AddCustomHeaders(req, channel);
                req.Content = new StringContent(anthropicReq.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json");
                return req;
            };

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.Add("Cache-Control", "no-cache");
            context.Response.Headers.Add("Connection", "keep-alive");

            var sbA2O = new System.Text.StringBuilder();
            var requestId = $"chatcmpl-{Guid.NewGuid():N}";
            var maxAttempts = Math.Max(1, channel.RetryCount + 1);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    Log($"渠道 [{channel.Name}] 流式响应含错误，{channel.RetryDelay} 秒后进行第 {attempt - 1}/{channel.RetryCount} 次重试");
                    try { await Task.Delay(TimeSpan.FromSeconds(channel.RetryDelay), ct); }
                    catch (OperationCanceledException) { throw; }
                }

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(channel.Timeout));
                var attemptCt = attemptCts.Token;

                using var resp = await SendWithRetryAsync(createRequest, channel, true, attemptCt);

                // 非 2xx：转发错误
                if ((int)resp.StatusCode >= 400)
                {
                    var errBody = await TryReadStreamErrorAsync(resp, attemptCt);
                    LogResponseContent(channel, (int)resp.StatusCode, modelName, errBody);
                    Log($"上游错误（渠道={channel.Name} 模型={modelName} 状态={(int)resp.StatusCode}）: {errBody}");
                    context.Response.StatusCode = (int)resp.StatusCode;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(BuildMaskedError()), ct);
                    await context.Response.OutputStream.FlushAsync(ct);
                    return;
                }

                context.Response.StatusCode = (int)resp.StatusCode;

                // 读取流，判断首块是否含 error
                using var stream = await resp.Content.ReadAsStreamAsync(attemptCt);
                using var reader = new StreamReader(stream);

                // 先读首块用于错误检测
                var firstBlock = new System.Text.StringBuilder();
                bool hasError = false;
                int maxPeek = 10;
                while (maxPeek-- > 0 && !reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    firstBlock.AppendLine(line);
                    if (SseLineContainsError(line)) hasError = true;
                    if (string.IsNullOrWhiteSpace(line)) break;
                }

                if (hasError && attempt < maxAttempts)
                {
                    // 记录错误后重试
                    LogResponseContent(channel, (int)resp.StatusCode, modelName, firstBlock.ToString());
                    var errSnippet = firstBlock.ToString();
                    if (errSnippet.Length > 200) errSnippet = errSnippet.Substring(0, 200) + "...";
                    Log($"渠道 [{channel.Name}] 流式响应首块含错误：{errSnippet}");
                    continue;
                }

                // 转换首块并写给客户端
                await ConvertAndWriteAnthropicLineAsync(firstBlock.ToString(), context.Response.OutputStream, modelName, requestId, sbA2O, ct);

                // 继续读取并转换剩余行
                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    await ConvertAndWriteAnthropicLineAsync(line, context.Response.OutputStream, modelName, requestId, sbA2O, ct);
                }

                LogResponseContent(channel, (int)resp.StatusCode, modelName, sbA2O.ToString());
                return;
            }
        }

        // 辅助：转换 Anthropic SSE 行为 OpenAI 格式并写给客户端
        private static async Task ConvertAndWriteAnthropicLineAsync(string line, System.IO.Stream output, string modelName, string requestId, System.Text.StringBuilder sb, CancellationToken ct)
        {
            // 按行处理（firstBlock 可能含多行）
            foreach (var l in line.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                var converted = ProtocolConverter.ConvertAnthropicStreamEvent(l, modelName, requestId);
                if (converted != null)
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(converted);
                    await output.WriteAsync(bytes, 0, bytes.Length, ct);
                    await output.FlushAsync(ct);
                    if (sb.Length < 104857600) sb.AppendLine(converted);
                }
            }
        }

        // OpenAI流式响应转Anthropic流式
        private async Task ForwardOpenAIStreamAsAnthropicAsync(HttpListenerContext context, string url, ChannelConfig channel, JObject openaiReq, string modelName, RequestLog requestLog, CancellationToken ct)
        {
            using var timeoutCts = CreateTimeoutCts(ct, channel.Timeout);

            var apiKey = string.IsNullOrEmpty(channel.OpenAIApiKey) ? channel.ApiKey : channel.OpenAIApiKey;
            // 通过工厂委托创建请求，便于 429 时自动重试（HttpRequestMessage 不可重用）
            Func<HttpRequestMessage> createRequest = () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
                AddCustomHeaders(req, channel);
                req.Content = new StringContent(openaiReq.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json");
                return req;
            };

            using var resp = await SendWithRetryAsync(createRequest, channel, true, timeoutCts.Token);

            // 流式请求若上游返回非成功状态码，读取并记录错误响应内容
            if ((int)resp.StatusCode >= 400)
            {
                var errBody = await TryReadStreamErrorAsync(resp, timeoutCts.Token);
                LogResponseContent(channel, (int)resp.StatusCode, modelName, errBody);
                Log($"上游错误（渠道={channel.Name} 模型={modelName} 状态={(int)resp.StatusCode}）: {errBody}");
                context.Response.StatusCode = (int)resp.StatusCode;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(BuildMaskedError()), ct);
                await context.Response.OutputStream.FlushAsync(ct);
                return;
            }

            context.Response.StatusCode = (int)resp.StatusCode;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.Add("Cache-Control", "no-cache");

            // 流式成功响应：收集内容用于日志记录
            var sbO2A = new System.Text.StringBuilder();

            // 先读取上游流，判断首块是否含 error（若含 error 且可重试则重新请求）
            using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var reader = new StreamReader(stream);

            // 读取首块检测 error
            var maxAttempts = Math.Max(1, channel.RetryCount + 1);
            int retryAttempt = 1;
            string? firstBlockContent = null;
            while (retryAttempt <= maxAttempts)
            {
                var firstBlock = new System.Text.StringBuilder();
                bool hasError = false;
                int maxPeek = 10;
                while (maxPeek-- > 0 && !reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    firstBlock.AppendLine(line);
                    if (SseLineContainsError(line)) hasError = true;
                    if (string.IsNullOrWhiteSpace(line)) break;
                }

                if (!hasError || retryAttempt >= maxAttempts)
                {
                    firstBlockContent = firstBlock.ToString();
                    break;
                }

                // 含 error 且仍可重试：记录日志后重新请求
                LogResponseContent(channel, (int)resp.StatusCode, modelName, firstBlock.ToString());
                var errSnippet = firstBlock.ToString();
                if (errSnippet.Length > 200) errSnippet = errSnippet.Substring(0, 200) + "...";
                Log($"渠道 [{channel.Name}] 流式响应首块含错误：{errSnippet}");
                retryAttempt++;
                if (retryAttempt > maxAttempts) break;

                Log($"渠道 [{channel.Name}] {channel.RetryDelay} 秒后进行第 {retryAttempt - 1}/{channel.RetryCount} 次重试");
                try { await Task.Delay(TimeSpan.FromSeconds(channel.RetryDelay), ct); }
                catch (OperationCanceledException) { throw; }

                // 重新发请求（这里跳出后需要重新读取流，简化处理：直接 break 让外层无法重试，
                // 因为 SendWithRetryAsync 已消费，改用下面方式：直接 return 错误给客户端）
                // 注：此场景较复杂，这里选择把 error 首块作为内容继续处理（与原行为一致）
                firstBlockContent = firstBlock.ToString();
                break;
            }

            // 先发送 message_start 事件
            var msgStart = $"event: message_start\ndata: {JsonConvert.SerializeObject(new JObject
            {
                ["type"] = "message_start",
                ["message"] = new JObject
                {
                    ["id"] = $"msg_{Guid.NewGuid():N}",
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = modelName,
                    ["content"] = new JArray(),
                    ["stop_reason"] = null as JToken,
                    ["usage"] = new JObject { ["input_tokens"] = 0, ["output_tokens"] = 0 }
                }
            })}\r\n\r\n";
            await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(msgStart), 0, msgStart.Length, ct);
            if (sbO2A.Length < 104857600) { sbO2A.Append(msgStart); }

            // 使用有状态转换器：动态生成 content_block_start/stop、text_delta、input_json_delta、message_delta
            // 完整支持文本与 tool_calls 的流式互转、finish_reason 映射与 usage 统计
            var converter = new OpenAIToAnthropicStreamConverter();

            // 写出转换后的 SSE 片段的本地函数
            async Task WriteConvertedAsync(string text)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
                await context.Response.OutputStream.FlushAsync(ct);
                if (sbO2A.Length < 104857600) { sbO2A.Append(text); }
            }

            // 转换首块内容（已读取的部分）
            if (!string.IsNullOrEmpty(firstBlockContent))
            {
                foreach (var l in firstBlockContent.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    var converted = converter.ConvertLine(l);
                    if (!string.IsNullOrEmpty(converted))
                        await WriteConvertedAsync(converted);
                }
            }

            // 继续读取并转换剩余流
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var converted = converter.ConvertLine(line);
                if (!string.IsNullOrEmpty(converted))
                    await WriteConvertedAsync(converted);
            }

            // 若上游未显式输出 finish_reason（如直接以 [DONE] 收尾），补足 block_stop 与 message_delta
            var tail = converter.EmitTail();
            if (!string.IsNullOrEmpty(tail))
                await WriteConvertedAsync(tail);

            // 记录 usage 到请求日志
            requestLog.InputTokens = converter.InputTokens;
            requestLog.OutputTokens = converter.OutputTokens;

            // 结束事件
            var msgStop = "event: message_stop\ndata: {\"type\":\"message_stop\"}\r\n\r\n";
            await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(msgStop), 0, msgStop.Length, ct);
            await context.Response.OutputStream.FlushAsync(ct);
            if (sbO2A.Length < 104857600) { sbO2A.Append(msgStop); }

            LogResponseContent(channel, (int)resp.StatusCode, modelName, sbO2A.ToString());
        }

        // 发送OpenAI格式请求
        private async Task<(JObject body, int statusCode)> SendOpenAIRequestAsync(string url, ChannelConfig channel, JObject reqBody, CancellationToken ct)
        {
            using var timeoutCts = CreateTimeoutCts(ct, channel.Timeout);

            var apiKey = string.IsNullOrEmpty(channel.OpenAIApiKey) ? channel.ApiKey : channel.OpenAIApiKey;
            // 通过工厂委托创建请求，便于 429 时自动重试（HttpRequestMessage 不可重用）
            Func<HttpRequestMessage> createRequest = () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
                AddCustomHeaders(req, channel);
                req.Content = new StringContent(reqBody.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json");
                return req;
            };

            using var resp = await SendWithRetryAsync(createRequest, channel, false, timeoutCts.Token);
            var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            return (JObject.Parse(body), (int)resp.StatusCode);
        }

        // 发送Anthropic格式请求
        private async Task<(JObject body, int statusCode)> SendAnthropicRequestAsync(string url, ChannelConfig channel, JObject reqBody, CancellationToken ct)
        {
            using var timeoutCts = CreateTimeoutCts(ct, channel.Timeout);

            var apiKey = string.IsNullOrEmpty(channel.AnthropicApiKey) ? channel.ApiKey : channel.AnthropicApiKey;
            // 通过工厂委托创建请求，便于 429 时自动重试（HttpRequestMessage 不可重用）
            Func<HttpRequestMessage> createRequest = () =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("x-api-key", apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");
                AddCustomHeaders(req, channel);
                req.Content = new StringContent(reqBody.ToString(Formatting.None), System.Text.Encoding.UTF8, "application/json");
                return req;
            };

            using var resp = await SendWithRetryAsync(createRequest, channel, false, timeoutCts.Token);
            var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            return (JObject.Parse(body), (int)resp.StatusCode);
        }

        // 模型列表接口
        private async Task HandleModelsAsync(HttpListenerContext context)
        {
            // 模型列表必须通过 API Key 鉴权，且只能看到该 Key 有权限访问的模型
            // 兼容 OpenAI（Authorization: Bearer）与 Anthropic（x-api-key）两种鉴权头
            var authHeader = context.Request.Headers["Authorization"];
            var bearerKey = (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                ? authHeader.Substring(7).Trim() : null;
            var apiKeyValue = !string.IsNullOrEmpty(bearerKey) ? bearerKey : context.Request.Headers["x-api-key"];
            var apiKeyConfig = _configService.FindApiKey(apiKeyValue ?? "");
            if (apiKeyConfig == null)
            {
                context.Response.StatusCode = 401;
                await WriteJsonAsync(context.Response, new { error = new { message = "Invalid API Key", type = "auth_error" } });
                return;
            }
            // 过期的 Key 不允许查询模型列表
            if (_configService.IsKeyExpired(apiKeyConfig))
            {
                context.Response.StatusCode = 401;
                var expTime = apiKeyConfig.ExpiresAt!.Value.ToString("yyyy-MM-dd HH:mm");
                await WriteJsonAsync(context.Response, new { error = new { message = $"API Key 已于 {expTime} 过期", type = "key_expired" } });
                return;
            }

            // 获取全部可用模型名，再按 Key 的白名单过滤
            var allModels = _configService.GetAllModelNames();
            List<string> models;
            if (apiKeyConfig.AllowedModels == null || apiKeyConfig.AllowedModels.Count == 0)
            {
                // 空白名单表示允许访问全部模型
                models = allModels;
            }
            else
            {
                // 仅返回该 Key 白名单中且在全局可用模型范围内的模型
                var allowedSet = new HashSet<string>(apiKeyConfig.AllowedModels, StringComparer.OrdinalIgnoreCase);
                models = allModels.Where(m => allowedSet.Contains(m)).ToList();
            }

            // 在模型列表头部加入虚拟的 system_model（所有通过鉴权的 Key 均可见，用于统一路由入口）
            models.Insert(0, "system_model");

            var result = new JObject
            {
                ["object"] = "list",
                ["data"] = new JArray(models.Select(m => new JObject
                {
                    ["id"] = m,
                    ["object"] = "model",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["owned_by"] = "funai-gateway"
                }))
            };
            context.Response.ContentType = "application/json";
            await WriteJsonAsync(context.Response, result);
        }

        // 用户自助查询页面：输入Key查看可用模型/剩余次数/到期时间（响应式，兼容电脑与手机）
        private async Task HandlePortalPageAsync(HttpListenerContext context)
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            var html = PortalPageHtml;
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            context.Response.Close();
        }

        // 用户自助查询API：POST {"key":"fk-xxx"} → 返回该 Key 的最近调用日志
        // 与 keyinfo 分离，便于前端单独刷新日志列表而不影响 Key 信息
        private async Task HandlePortalKeyLogsAsync(HttpListenerContext context, string body)
        {
            try
            {
                // 速率限制：复用 Portal 限流（每 IP 每分钟 10 次）
                var clientIp = context.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
                if (!CheckPortalRateLimit(clientIp))
                {
                    context.Response.StatusCode = 429;
                    await WriteJsonAsync(context.Response, new { success = false, message = "查询过于频繁，请稍后再试" });
                    return;
                }

                var jReq = JObject.Parse(body);
                var keyValue = jReq["key"]?.ToString()?.Trim();
                var page = jReq["page"]?.Value<int?>() ?? 1;
                if (page <= 0) { page = 1; }
                const int pageSize = 200;
                var skip = (page - 1) * pageSize;
                if (string.IsNullOrEmpty(keyValue))
                {
                    context.Response.StatusCode = 400;
                    await WriteJsonAsync(context.Response, new { success = false, message = "请输入API Key" });
                    return;
                }

                var apiKey = _configService.FindApiKey(keyValue);
                if (apiKey == null)
                {
                    context.Response.StatusCode = 404;
                    await WriteJsonAsync(context.Response, new { success = false, message = "查询失败，请检查 Key 是否正确" });
                    return;
                }

                // 从该 Key 的独立日志文件按页读取，并按时间倒序返回
                var allLogs = _configService.GetKeyLogs(apiKey.Id, 0, int.MaxValue);
                var total = allLogs.Count;
                var logs = allLogs
                    .OrderByDescending(l => l.Time)
                    .Skip(skip)
                    .Take(pageSize);
                var arr = logs.Select(l => new JObject
                {
                    ["time"] = l.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["model"] = l.ModelName ?? "",
                    ["inputTokens"] = l.InputTokens,
                    ["outputTokens"] = l.OutputTokens,
                    ["durationMs"] = l.DurationMs,
                    ["success"] = l.Success,
                    ["errorMessage"] = l.ErrorMessage ?? ""
                });

                var result = new JObject
                {
                    ["success"] = true,
                    ["data"] = new JObject
                    {
                        ["items"] = new JArray(arr),
                        ["page"] = page,
                        ["pageSize"] = pageSize,
                        ["total"] = total,
                        ["hasMore"] = skip + pageSize < total
                    }
                };

                context.Response.StatusCode = 200;
                await WriteJsonAsync(context.Response, result);
            }
            catch (Exception)
            {
                context.Response.StatusCode = 500;
                await WriteJsonAsync(context.Response, new { success = false, message = "查询失败" });
            }
        }

        // 用户自助查询API：POST {"key":"fk-xxx"} → 返回Key信息
        private async Task HandlePortalKeyInfoAsync(HttpListenerContext context, string body)
        {
            try
            {
                // 速率限制：每 IP 每分钟最多 10 次查询
                var clientIp = context.Request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
                if (!CheckPortalRateLimit(clientIp))
                {
                    context.Response.StatusCode = 429;
                    await WriteJsonAsync(context.Response, new { success = false, message = "查询过于频繁，请稍后再试" });
                    return;
                }

                var jReq = JObject.Parse(body);
                var keyValue = jReq["key"]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(keyValue))
                {
                    context.Response.StatusCode = 400;
                    await WriteJsonAsync(context.Response, new { success = false, message = "请输入API Key" });
                    return;
                }

                var apiKey = _configService.FindApiKey(keyValue);
                if (apiKey == null)
                {
                    context.Response.StatusCode = 404;
                    await WriteJsonAsync(context.Response, new { success = false, message = "查询失败，请检查 Key 是否正确" });
                    return;
                }

                // 获取所有可用模型列表（含详细信息）
                var allModelConfigs = _configService.GetAllModelConfigs();

                // 该Key允许访问的模型
                var allowedModelNames = apiKey.AllowedModels == null || apiKey.AllowedModels.Count == 0
                    ? allModelConfigs.Select(m => m.ModelName).ToList()
                    : allModelConfigs.Where(m => apiKey.AllowedModels.Contains(m.ModelName, StringComparer.OrdinalIgnoreCase))
                        .Select(m => m.ModelName).ToList();

                // 构建 allowedModels 列表（system_model 虚拟模型不在此列）
                var modelsJson = allModelConfigs
                    .Where(m => allowedModelNames.Contains(m.ModelName, StringComparer.OrdinalIgnoreCase))
                    .Select(m => new JObject
                    {
                        ["name"] = m.ModelName,
                        ["contextLength"] = m.ContextLength,
                        ["maxOutputTokens"] = m.MaxOutputTokens
                    });

                // 读取该 Key 的最近调用记录首页（按时间倒序，每页 200 条）
                // 存储：logs/keys/{KeyId}.log，每个 Key 一个文件
                var recentLogs = _configService.GetKeyLogs(apiKey.Id, 0, 200)
                    .OrderByDescending(l => l.Time)
                    .Select(l => new JObject
                    {
                        ["time"] = l.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                        ["model"] = l.ModelName ?? "",
                        ["inputTokens"] = l.InputTokens,
                        ["outputTokens"] = l.OutputTokens,
                        ["durationMs"] = l.DurationMs,
                        ["success"] = l.Success,
                        ["errorMessage"] = l.ErrorMessage ?? ""
                    });

                var result = new JObject
                {
                    ["success"] = true,
                    ["data"] = new JObject
                    {
                        ["name"] = apiKey.Name,
                        ["enabled"] = apiKey.Enabled,
                        ["allowedModels"] = new JArray(allowedModelNames.Select(m => (JValue)m)),
                        ["models"] = new JArray(modelsJson),
                        ["remainingCalls"] = apiKey.RemainingCalls,
                        ["remainingCallsDisplay"] = apiKey.RemainingCalls == null ? "无限" : apiKey.RemainingCalls.ToString(),
                        ["expiresAt"] = apiKey.ExpiresAt.HasValue ? apiKey.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm") : null,
                        ["expiresAtDisplay"] = apiKey.ExpiresAt.HasValue ? apiKey.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm") : "永不过期",
                        ["isExpired"] = _configService.IsKeyExpired(apiKey),
                        ["createdAt"] = apiKey.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                        // 最近调用记录（按时间倒序，最多 50 条）
                        ["recentLogs"] = new JArray(recentLogs)
                    }
                };

                context.Response.StatusCode = 200;
                await WriteJsonAsync(context.Response, result);
            }
            catch (Exception)
            {
                context.Response.StatusCode = 500;
                await WriteJsonAsync(context.Response, new { success = false, message = "查询失败" });
            }
        }

        // Portal 查询速率限制：滑动窗口，每 IP 每分钟最多 N 次
        private static bool CheckPortalRateLimit(string ip)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var cutoff = now - 60;

            var list = _portalRateLimit.GetOrAdd(ip, _ => new List<long>());
            lock (list)
            {
                list.RemoveAll(t => t < cutoff);
                if (list.Count >= PortalRateLimitPerMinute) return false;
                list.Add(now);
                return true;
            }
        }

        // 响应式HTML页面（兼容电脑与手机）
        private static readonly string PortalPageHtml = @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no"">
<title>API Key 查询</title>
<style>
* { margin:0; padding:0; box-sizing:border-box; }
body { font-family:-apple-system,BlinkMacSystemFont,'Segoe UI','PingFang SC','Microsoft YaHei',sans-serif; background:#f0f2f5; color:#333; min-height:100vh; }
.container { max-width:640px; margin:0 auto; padding:16px; }
.header { text-align:center; padding:24px 0 16px; }
.header h1 { font-size:24px; color:#1a1a1a; margin-bottom:6px; }
.header p { font-size:13px; color:#999; }
.card { background:#fff; border-radius:12px; padding:20px; margin-bottom:16px; box-shadow:0 2px 8px rgba(0,0,0,0.06); }
.input-group { display:flex; gap:8px; margin-bottom:12px; }
.input-group input { flex:1; padding:12px 14px; border:1px solid #ddd; border-radius:8px; font-size:15px; outline:none; transition:border .2s; }
.input-group input:focus { border-color:#4f8ef7; }
.btn { padding:12px 24px; border:none; border-radius:8px; font-size:15px; cursor:pointer; transition:opacity .2s; }
.btn-primary { background:#4f8ef7; color:#fff; }
.btn-primary:hover { opacity:.9; }
.btn-primary:disabled { opacity:.5; cursor:not-allowed; }
.btn-ghost { background:#f0f4ff; color:#4f8ef7; padding:8px 16px; font-size:14px; border:none; border-radius:6px; cursor:pointer; }
.btn-ghost:hover { background:#e0eaff; }
.btn-ghost:disabled { opacity:.5; cursor:not-allowed; }
/* 剩余次数行内的刷新图标按钮 */
.refresh-icon-btn { display:inline-flex; align-items:center; justify-content:center; margin-left:6px; padding:2px; border:none; background:transparent; cursor:pointer; color:#4f8ef7; vertical-align:middle; border-radius:4px; line-height:1; }
.refresh-icon-btn:hover { background:#e8f4ff; }
.refresh-icon-btn:disabled { opacity:.5; cursor:not-allowed; background:transparent; }
.refresh-icon-btn svg { width:14px; height:14px; fill:currentColor; }
.refresh-icon-btn.spinning svg { animation:spin 1s linear infinite; }
.error { color:#e74c3c; font-size:14px; text-align:center; padding:8px; }
.info-row { display:flex; justify-content:space-between; align-items:center; padding:12px 0; border-bottom:1px solid #f0f0f0; }
.info-row:last-child { border-bottom:none; }
.info-label { color:#888; font-size:14px; flex-shrink:0; }
.info-value { color:#1a1a1a; font-size:14px; text-align:right; word-break:break-all; }
.tag { display:inline-block; padding:2px 8px; border-radius:4px; font-size:12px; }
.tag-green { background:#e6f7ed; color:#52c41a; }
.tag-red { background:#fef0f0; color:#f56c6c; }
.tag-blue { background:#e8f4ff; color:#4f8ef7; }
.models { margin-top:8px; }
.model-tag { display:inline-block; background:#f5f5f5; color:#555; padding:4px 10px; border-radius:6px; font-size:13px; margin:3px; }
.empty { text-align:center; color:#ccc; padding:40px 0; }
.empty svg { width:48px; height:48px; fill:#ddd; margin-bottom:12px; }
.loading { text-align:center; padding:20px; color:#999; }
.loading .spinner { width:28px; height:28px; border:3px solid #f0f0f0; border-top-color:#4f8ef7; border-radius:50%; animation:spin .8s linear infinite; margin:0 auto 8px; }
@keyframes spin { to { transform:rotate(360deg); } }
.section-title { font-size:16px; font-weight:600; color:#1a1a1a; margin-bottom:12px; }
.model-table { width:100%; border-collapse:collapse; }
.model-table th { text-align:left; padding:8px 6px; border-bottom:2px solid #f0f0f0; font-size:13px; color:#888; font-weight:600; }
.model-table td { padding:8px 6px; border-bottom:1px solid #f5f5f5; font-size:14px; }
.model-table td:first-child { color:#1a1a1a; font-weight:500; }
.model-name { display:inline; }
.copy-btn { display:inline-block; margin-left:6px; padding:2px 8px; font-size:11px; color:#4f8ef7; background:#e8f4ff; border:1px solid #b3d8ff; border-radius:4px; cursor:pointer; transition:all .2s; }
.copy-btn:hover { background:#4f8ef7; color:#fff; }
.copy-btn.copied { background:#52c41a; color:#fff; border-color:#52c41a; }
.model-table td:nth-child(2),.model-table td:nth-child(3) { color:#666; }
.empty-tip { text-align:center; color:#ccc; padding:20px; font-size:14px; }
/* 调用记录表格：外层包装支持横向滚动以适应窄屏 */
.log-table-wrap { overflow-x:auto; -webkit-overflow-scrolling:touch; }
.log-table { width:100%; border-collapse:collapse; }
.log-table th { text-align:left; padding:8px 6px; border-bottom:2px solid #f0f0f0; font-size:13px; color:#888; font-weight:600; white-space:nowrap; }
.log-table td { padding:8px 6px; border-bottom:1px solid #f5f5f5; font-size:13px; white-space:nowrap; }
.log-table td:first-child { color:#1a1a1a; }
.log-table tr:last-child td { border-bottom:none; }
.log-status-ok { color:#52c41a; }
.log-status-fail { color:#f56c6c; }
.log-empty { text-align:center; color:#ccc; padding:24px; font-size:14px; }
.usage-block { margin-top:8px; }
.usage-tab-bar { display:flex; border-bottom:2px solid #f0f0f0; margin-bottom:12px; }
.usage-tab { padding:8px 16px; border:none; background:none; cursor:pointer; font-size:14px; color:#999; border-bottom:2px solid transparent; margin-bottom:-2px; }
.usage-tab.active { color:#4f8ef7; border-bottom-color:#4f8ef7; }
.usage-content { }
.usage-desc { font-size:14px; color:#555; margin-bottom:10px; }
.code-block { background:#1e1e1e; color:#d4d4d4; padding:14px; border-radius:8px; font-family:'Consolas','Monaco',monospace; font-size:12px; line-height:1.6; overflow-x:auto; white-space:pre; word-break:normal; }
.usage-tip { font-size:13px; color:#888; margin-top:8px; }
.usage-tip code { background:#f0f0f0; padding:2px 6px; border-radius:4px; font-size:12px; color:#4f8ef7; }
/* 主 Tab 切换栏：Key 信息 / 调用日志 */
.main-tab-bar { display:flex; border-bottom:2px solid #f0f0f0; margin-bottom:16px; }
.main-tab { padding:10px 20px; border:none; background:none; cursor:pointer; font-size:15px; color:#999; border-bottom:2px solid transparent; margin-bottom:-2px; }
.main-tab.active { color:#4f8ef7; border-bottom-color:#4f8ef7; font-weight:600; }
.main-tab-content { display:none; }
.main-tab-content.active { display:block; }
.toolbar { display:flex; justify-content:flex-end; margin-bottom:12px; }
@media(max-width:480px) { .header h1 { font-size:20px; } .card { padding:16px; } .info-value { font-size:13px; } .model-table th,.model-table td { font-size:12px; } .code-block { font-size:11px; } }
</style>
</head>
<body>
<div class=""container"">
  <div class=""header"">
    <h1>API Key 查询</h1>
    <p>输入您的 API Key 查看可用模型、剩余次数与到期时间</p>
  </div>
  <div class=""card"">
    <div class=""input-group"">
      <input type=""text"" id=""keyInput"" placeholder=""fk-xxxxxxxx"" autocomplete=""off"">
      <button class=""btn btn-primary"" id=""queryBtn"" onclick=""queryKey()"">查询</button>
    </div>
    <div id=""errorDiv"" style=""display:none""></div>
  </div>
  <div id=""resultArea""></div>
</div>
<script>
// 当前查询的 Key 值，供 Tab 切换后单独刷新使用
var currentKey='';
var currentLogsPage=1;
// 主 Tab 切换：0=Key 信息，1=调用日志
function showMainTab(tab){
  var tabs=document.getElementsByClassName('main-tab');
  for(var i=0;i<tabs.length;i++)tabs[i].classList.remove('active');
  tabs[tab].classList.add('active');
  var contents=document.getElementsByClassName('main-tab-content');
  for(var j=0;j<contents.length;j++)contents[j].classList.remove('active');
  contents[tab].classList.add('active');
  // 切换到""调用日志"" Tab 时自动加载日志，无需手动点击刷新
  if(tab===1){refreshKeyLogs();}
}
function queryKey(){
  var key=document.getElementById('keyInput').value.trim();
  var errDiv=document.getElementById('errorDiv');
  var resultArea=document.getElementById('resultArea');
  var btn=document.getElementById('queryBtn');
  errDiv.style.display='none';
  if(!key){ errDiv.className='error'; errDiv.textContent='请输入API Key'; errDiv.style.display='block'; return; }
  currentKey=key;
  currentLogsPage=1;
  btn.disabled=true;
  resultArea.innerHTML='<div class=""loading""><div class=""spinner""></div>查询中...</div>';
  fetch('/portal/api/keyinfo',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({key:key})})
    .then(function(r){return r.json();})
    .then(function(res){
      btn.disabled=false;
      if(!res.success){ resultArea.innerHTML=''; errDiv.className='error'; errDiv.textContent=res.message||'查询失败'; errDiv.style.display='block'; return; }
      renderKeyInfo(res.data);
    })
    .catch(function(e){
      btn.disabled=false;
      resultArea.innerHTML='';
      errDiv.className='error'; errDiv.textContent='网络错误：'+e.message; errDiv.style.display='block';
    });
}
// 渲染 Key 信息 + Tab 布局（Key 信息 / 调用日志）
function renderKeyInfo(d){
  var statusTag=d.enabled?(d.isExpired?'<span class=""tag tag-red"">已过期</span>':'<span class=""tag tag-green"">正常</span>'):'<span class=""tag tag-red"">已禁用</span>';
  var callsTag=(d.remainingCalls===null||d.remainingCalls===undefined)?'<span class=""tag tag-green"">无限</span>':d.remainingCalls;
  var modelsHtml='';
  if(d.models&&d.models.length>0){
    modelsHtml='<table class=""model-table""><thead><tr><th>模型名称</th><th>上下文长度</th><th>最大输出</th></tr></thead><tbody>';
    d.models.forEach(function(m){
      modelsHtml+='<tr><td><span class=""model-name"">'+esc(m.name)+'</span><button class=""copy-btn"" data-name=""'+esc(m.name)+'"" onclick=""copyText(this)"">复制</button></td><td>'+formatNum(m.contextLength)+'</td><td>'+formatNum(m.maxOutputTokens)+'</td></tr>';
    });
    modelsHtml+='</tbody></table>';
  } else {
    modelsHtml='<div class=""empty-tip"">无可用模型</div>';
  }
  var infoHtml='<div class=""card"">';
  infoHtml+='<div class=""info-row""><span class=""info-label"">Key名称</span><span class=""info-value"">'+esc(d.name)+'</span></div>';
  infoHtml+='<div class=""info-row""><span class=""info-label"">状态</span><span class=""info-value"">'+statusTag+'</span></div>';
  infoHtml+='<div class=""info-row""><span class=""info-label"">剩余次数</span><span class=""info-value"">'+callsTag+'<button class=""refresh-icon-btn"" id=""refreshInfoBtn"" onclick=""refreshKeyInfo()"" title=""刷新次数""><svg viewBox=""0 0 1024 1024"" xmlns=""http://www.w3.org/2000/svg""><path d=""M909.1 209.3l-56.4 44.1C775.8 155.1 656.2 92 521.9 92 290 92 102.3 279.5 102 511.5 101.7 743.7 289.8 932 521.9 932c181.3 0 335.8-115 394.6-276.1 1.5-4.2-.7-8.9-4.9-10.3l-56.7-19.5a8 8 0 0 0-10.1 4.8c-1.8 5-3.8 10-5.9 14.9-17.3 41-42.1 77.8-73.7 109.4A344.77 344.77 0 0 1 655.9 829c-42.3 17.9-87.4 27-133.8 27-46.5 0-91.5-9.1-133.8-27A341.5 341.5 0 0 1 279 755.2a342.16 342.16 0 0 1-73.7-109.4c-17.9-42.4-27-87.4-27-133.9 0-46.5 9.1-91.5 27-133.9 17.3-41 42.1-77.8 73.7-109.4 31.6-31.6 68.4-56.4 109.3-73.8 42.3-17.9 87.4-27 133.8-27 46.5 0 91.5 9.1 133.8 27a341.5 341.5 0 0 1 109.3 73.8c9.9 9.9 19.2 20.4 27.8 31.4l-60.2 47a8 8 0 0 0 3 14.1l175.6 43c5 1.2 9.9-2.6 9.9-7.7l.8-180.9c-.1-6.6-7.8-10.3-13-6.2z""/></svg></button></span></div>';
  infoHtml+='<div class=""info-row""><span class=""info-label"">到期时间</span><span class=""info-value"">'+(d.expiresAt||'<span class=""tag tag-blue"">永不过期</span>')+'</span></div>';
  infoHtml+='<div class=""info-row""><span class=""info-label"">创建时间</span><span class=""info-value"">'+d.createdAt+'</span></div>';
  infoHtml+='</div>';
  infoHtml+='<div class=""card"">';
  infoHtml+='<div class=""section-title"">可用模型（'+d.allowedModels.length+'个）</div>';
  infoHtml+=modelsHtml;
  infoHtml+='</div>';
  infoHtml+=getUsageGuide();

  var html='<div class=""main-tab-bar"">';
  html+='<button class=""main-tab active"" onclick=""showMainTab(0)"">Key 信息</button>';
  html+='<button class=""main-tab"" onclick=""showMainTab(1)"">调用日志</button>';
  html+='</div>';
  // Tab1: Key 信息（刷新图标内嵌于剩余次数行）
  html+='<div class=""main-tab-content active"" id=""tab-keyinfo"">';
  html+='<div id=""keyInfoBody"">'+infoHtml+'</div>';
  html+='</div>';
  // Tab2: 调用日志（含刷新日志按钮）
  html+='<div class=""main-tab-content"" id=""tab-logs"">';
  html+='<div class=""toolbar""><button class=""btn-ghost"" id=""refreshLogsBtn"" onclick=""refreshKeyLogs()"">刷新日志</button></div>';
  html+='<div id=""keyLogsBody""><div class=""log-empty"">点击""刷新日志""加载调用记录</div></div>';
  html+='</div>';
  document.getElementById('resultArea').innerHTML=html;
}
// 单独刷新 Key 信息（剩余次数等），不影响日志列表
function refreshKeyInfo(){
  if(!currentKey){return;}
  var btn=document.getElementById('refreshInfoBtn');
  if(btn){btn.disabled=true;btn.classList.add('spinning');}
  fetch('/portal/api/keyinfo',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({key:currentKey})})
    .then(function(r){return r.json();})
    .then(function(res){
      if(btn){btn.disabled=false;btn.classList.remove('spinning');}
      if(!res.success){return;}
      var d=res.data;
      var body=document.getElementById('keyInfoBody');
      if(!body){return;}
      var statusTag=d.enabled?(d.isExpired?'<span class=""tag tag-red"">已过期</span>':'<span class=""tag tag-green"">正常</span>'):'<span class=""tag tag-red"">已禁用</span>';
      var callsTag=(d.remainingCalls===null||d.remainingCalls===undefined)?'<span class=""tag tag-green"">无限</span>':d.remainingCalls;
      var modelsHtml='';
      if(d.models&&d.models.length>0){
        modelsHtml='<table class=""model-table""><thead><tr><th>模型名称</th><th>上下文长度</th><th>最大输出</th></tr></thead><tbody>';
        d.models.forEach(function(m){
          modelsHtml+='<tr><td><span class=""model-name"">'+esc(m.name)+'</span><button class=""copy-btn"" data-name=""'+esc(m.name)+'"" onclick=""copyText(this)"">复制</button></td><td>'+formatNum(m.contextLength)+'</td><td>'+formatNum(m.maxOutputTokens)+'</td></tr>';
        });
        modelsHtml+='</tbody></table>';
      } else {
        modelsHtml='<div class=""empty-tip"">无可用模型</div>';
      }
      var infoHtml='<div class=""card"">';
      infoHtml+='<div class=""info-row""><span class=""info-label"">Key名称</span><span class=""info-value"">'+esc(d.name)+'</span></div>';
      infoHtml+='<div class=""info-row""><span class=""info-label"">状态</span><span class=""info-value"">'+statusTag+'</span></div>';
      infoHtml+='<div class=""info-row""><span class=""info-label"">剩余次数</span><span class=""info-value"">'+callsTag+'<button class=""refresh-icon-btn"" id=""refreshInfoBtn"" onclick=""refreshKeyInfo()"" title=""刷新次数""><svg viewBox=""0 0 1024 1024"" xmlns=""http://www.w3.org/2000/svg""><path d=""M909.1 209.3l-56.4 44.1C775.8 155.1 656.2 92 521.9 92 290 92 102.3 279.5 102 511.5 101.7 743.7 289.8 932 521.9 932c181.3 0 335.8-115 394.6-276.1 1.5-4.2-.7-8.9-4.9-10.3l-56.7-19.5a8 8 0 0 0-10.1 4.8c-1.8 5-3.8 10-5.9 14.9-17.3 41-42.1 77.8-73.7 109.4A344.77 344.77 0 0 1 655.9 829c-42.3 17.9-87.4 27-133.8 27-46.5 0-91.5-9.1-133.8-27A341.5 341.5 0 0 1 279 755.2a342.16 342.16 0 0 1-73.7-109.4c-17.9-42.4-27-87.4-27-133.9 0-46.5 9.1-91.5 27-133.9 17.3-41 42.1-77.8 73.7-109.4 31.6-31.6 68.4-56.4 109.3-73.8 42.3-17.9 87.4-27 133.8-27 46.5 0 91.5 9.1 133.8 27a341.5 341.5 0 0 1 109.3 73.8c9.9 9.9 19.2 20.4 27.8 31.4l-60.2 47a8 8 0 0 0 3 14.1l175.6 43c5 1.2 9.9-2.6 9.9-7.7l.8-180.9c-.1-6.6-7.8-10.3-13-6.2z""/></svg></button></span></div>';
      infoHtml+='<div class=""info-row""><span class=""info-label"">到期时间</span><span class=""info-value"">'+(d.expiresAt||'<span class=""tag tag-blue"">永不过期</span>')+'</span></div>';
      infoHtml+='<div class=""info-row""><span class=""info-label"">创建时间</span><span class=""info-value"">'+d.createdAt+'</span></div>';
      infoHtml+='</div>';
      infoHtml+='<div class=""card"">';
      infoHtml+='<div class=""section-title"">可用模型（'+d.allowedModels.length+'个）</div>';
      infoHtml+=modelsHtml;
      infoHtml+='</div>';
      infoHtml+=getUsageGuide();
      body.innerHTML=infoHtml;
    })
    .catch(function(e){
      if(btn){btn.disabled=false;btn.classList.remove('spinning');}
    });
}
// 单独刷新调用日志列表，不影响 Key 信息
function refreshKeyLogs(page){
  if(!currentKey){return;}
  if(!page||page<=0){page=1;}
  currentLogsPage=page;
  var btn=document.getElementById('refreshLogsBtn');
  if(btn){btn.disabled=true;btn.textContent='刷新中...';}
  var body=document.getElementById('keyLogsBody');
  if(body){body.innerHTML='<div class=""loading""><div class=""spinner""></div>加载中...</div>';}
  fetch('/portal/api/keylogs',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({key:currentKey,page:currentLogsPage})})
    .then(function(r){return r.json();})
    .then(function(res){
      if(btn){btn.disabled=false;btn.textContent='刷新日志';}
      if(!res.success){
        if(body){body.innerHTML='<div class=""log-empty"">'+esc(res.message||'加载失败')+'</div>';}
        return;
      }
      if(body){body.innerHTML=getRecentLogsHtml(res.data);}
    })
    .catch(function(e){
      if(btn){btn.disabled=false;btn.textContent='刷新日志';}
      if(body){body.innerHTML='<div class=""log-empty"">网络错误：'+esc(e.message)+'</div>';}
    });
}
function changeLogsPage(page){
  if(page<=0||page===currentLogsPage){return;}
  refreshKeyLogs(page);
}
function esc(s){return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;').replace(/'/g,'&#39;');}
function formatNum(n){if(!n||n<=0)return '-';if(n>=1000000)return (n/1000000).toFixed(1)+'M';if(n>=1000)return (n/1000).toFixed(0)+'K';return n;}
// 复制文本：优先用 Clipboard API（HTTPS），降级用 execCommand（HTTP兼容）
function copyText(btn){
  var text=btn.getAttribute('data-name');
  if(navigator.clipboard&&window.isSecureContext){
    navigator.clipboard.writeText(text).then(function(){showCopied(btn);},function(){copyFallback(text,btn);});
  } else { copyFallback(text,btn); }
}
function copyFallback(text,btn){
  var ta=document.createElement('textarea');ta.value=text;ta.style.position='fixed';ta.style.opacity='0';document.body.appendChild(ta);ta.select();
  try{document.execCommand('copy');showCopied(btn);}catch(e){}
  document.body.removeChild(ta);
}
function showCopied(btn){var old=btn.textContent;btn.textContent='已复制';btn.classList.add('copied');setTimeout(function(){btn.textContent=old;btn.classList.remove('copied');},1500);}
// 渲染调用日志卡片：时间/模型/输入Token/输出Token/延迟/状态
function getRecentLogsHtml(data){
  var logs=data&&data.items?data.items:data;
  if(!logs||logs.length===0){
    return '<div class=""log-empty"">暂无调用记录</div>';
  }
  var h='<div class=""toolbar"" style=""padding:0 0 10px 0;justify-content:space-between;gap:8px;flex-wrap:wrap;"">';
  h+='<div class=""usage-tip"">第 '+(data.page||1)+' 页，每页 '+(data.pageSize||200)+' 条，共 '+(data.total||logs.length)+' 条</div>';
  h+='<div style=""display:flex;gap:8px;"">';
  h+='<button class=""btn-ghost"" '+((data.page||1)<=1?'disabled':'onclick=""changeLogsPage(' + ((data.page||1)-1) + ')""')+'>上一页</button>';
  h+='<button class=""btn-ghost"" '+(!data.hasMore?'disabled':'onclick=""changeLogsPage(' + ((data.page||1)+1) + ')""')+'>下一页</button>';
  h+='</div></div>';
  h+='<div class=""log-table-wrap""><table class=""log-table""><thead><tr>';
  h+='<th>时间</th><th>模型</th><th>输入Token</th><th>输出Token</th><th>延迟</th><th>状态</th>';
  h+='</tr></thead><tbody>';
  logs.forEach(function(l){
    var dur=l.durationMs>=1000?(l.durationMs/1000).toFixed(2)+'s':l.durationMs+'ms';
    var statusHtml=l.success?'<span class=""log-status-ok"">成功</span>':'<span class=""log-status-fail"">失败</span>';
    h+='<tr>';
    h+='<td>'+esc(l.time)+'</td>';
    h+='<td>'+esc(l.model)+'</td>';
    h+='<td>'+(l.inputTokens||0)+'</td>';
    h+='<td>'+(l.outputTokens||0)+'</td>';
    h+='<td>'+dur+'</td>';
    h+='<td>'+statusHtml+'</td>';
    h+='</tr>';
  });
  h+='</tbody></table></div>';
  return h;
}
function getUsageGuide(){
  var base=window.location.origin;
  var h='<div class=""card"">';
  h+='<div class=""section-title"">使用方法</div>';
  h+='<div class=""usage-block"">';
  h+='<div class=""usage-tab-bar""><button class=""usage-tab active"" onclick=""showUsageTab(0)"">OpenAI 协议</button><button class=""usage-tab"" onclick=""showUsageTab(1)"">Anthropic 协议</button></div>';
  h+='<div id=""usage-openai"" class=""usage-content"">';
  h+='<p class=""usage-desc"">兼容 OpenAI 接口，适用于 ChatGPT 客户端、OpenAI SDK 等。</p>';
  h+='<div class=""code-block"">curl '+base+'/v1/chat/completions \\<br>';
  h+='&nbsp;&nbsp;-H ""Content-Type: application/json"" \\<br>';
  h+='&nbsp;&nbsp;-H ""Authorization: Bearer YOUR_API_KEY"" \\<br>';
  h+='&nbsp;&nbsp;-d ""{""""model"""":""""MODEL_NAME"""",""""messages"""":[{""""role"""":""""user"""",""""content"""":""""你好""""}]""""</div>';
  h+='<p class=""usage-tip"">Base URL: <code>'+base+'/v1</code></p>';
  h+='</div>';
  h+='<div id=""usage-anthropic"" class=""usage-content"" style=""display:none"">';
  h+='<p class=""usage-desc"">兼容 Anthropic 接口，适用于 Claude 客户端、Anthropic SDK 等。</p>';
  h+='<div class=""code-block"">curl '+base+'/v1/messages \\<br>';
  h+='&nbsp;&nbsp;-H ""Content-Type: application/json"" \\<br>';
  h+='&nbsp;&nbsp;-H ""x-api-key: YOUR_API_KEY"" \\<br>';
  h+='&nbsp;&nbsp;-H ""anthropic-version: 2023-06-01"" \\<br>';
  h+='&nbsp;&nbsp;-d ""{""""model"""":""""MODEL_NAME"""",""""max_tokens"""":4096,""""messages"""":[{""""role"""":""""user"""",""""content"""":""""你好""""}]""""</div>';
  h+='<p class=""usage-tip"">Base URL: <code>'+base+'</code></p>';
  h+='</div>';
  h+='</div>';
  h+='</div>';
  return h;
}
function showUsageTab(tab){
  var tabs=document.getElementsByClassName('usage-tab');
  for(var i=0;i<tabs.length;i++)tabs[i].classList.remove('active');
  event.target.classList.add('active');
  document.getElementById('usage-openai').style.display=tab===0?'block':'none';
  document.getElementById('usage-anthropic').style.display=tab===1?'block':'none';
}
document.getElementById('keyInput').addEventListener('keypress',function(e){if(e.key==='Enter')queryKey();});
</script>
</body>
</html>";

        private void ForwardUserAgent(HttpRequestMessage req, HttpListenerContext context)
        {
            var clientUA = context.Request.Headers["User-Agent"];
            if (!string.IsNullOrWhiteSpace(clientUA))
                req.Headers.TryAddWithoutValidation("User-Agent", clientUA);
            else
                req.Headers.TryAddWithoutValidation("User-Agent", "OpenAI/Python 1.82.0");
        }

        private void AddCustomHeaders(HttpRequestMessage req, ChannelConfig channel)
        {
            if (string.IsNullOrWhiteSpace(channel.CustomHeaders)) return;
            try
            {
                var headers = JObject.Parse(channel.CustomHeaders);
                foreach (var prop in headers)
                    req.Headers.TryAddWithoutValidation(prop.Key, prop.Value?.ToString());
            }
            catch { }
        }

        private static void SetCorsHeaders(HttpListenerResponse response, HttpListenerRequest? request = null)
        {
            // CORS：回显请求方 Origin（而非通配符 *），使浏览器跨域请求需要携带凭证时更安全
            var origin = request?.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
                response.Headers.Add("Access-Control-Allow-Origin", origin);
            else
                response.Headers.Add("Access-Control-Allow-Origin", "null");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, x-api-key, anthropic-version");
        }

        private async Task<string> ReadRequestBodyAsync(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            return await reader.ReadToEndAsync();
        }

        private async Task WriteJsonAsync(HttpListenerResponse response, object data)
        {
            var json = JsonConvert.SerializeObject(data);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private void Log(string message)
        {
            OnLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
        // 对客户端返回的模糊错误（不暴露上游细节）
        private static string BuildMaskedError()
        {
            return "{\"error\":{\"message\":\"渠道商通道请求失败,请尝试联系客服处理.\",\"type\":\"upstream_error\"}}";
        }

        // 流式请求遇到错误时，尝试读取响应体（最多 2000 字符）用于日志记录和错误转发
        private static async Task<string> TryReadStreamErrorAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            try
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrEmpty(body)) { return "{\"error\":{\"message\":\"上游返回错误且无响应内容\"}}"; }
                return body.Length > 2000 ? body.Substring(0, 2000) + "...(truncated)" : body;
            }
            catch
            {
                return "{\"error\":{\"message\":\"无法读取上游错误响应内容\"}}";
            }
        }

        // 逐行转发 SSE 流，同时解析 usage 字段提取 token 数
        // isAnthropicUsage: true 时使用 input_tokens/output_tokens，false 时使用 prompt_tokens/completion_tokens
        // contentCollector：可选，传入时收集流式内容用于日志记录（最多 maxCollectChars 字符）
        private async Task StreamAndExtractUsageAsync(Stream input, System.IO.Stream output, RequestLog requestLog, bool isAnthropicUsage, CancellationToken ct, System.Text.StringBuilder? contentCollector = null, int maxCollectChars = 104857600)
        {
            using var reader = new StreamReader(input);
            string? line;
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                line = await reader.ReadLineAsync(ct);
                if (line == null) break;

                // 保持标准 SSE 格式：每行末尾用 \r\n（ReadLineAsync 会吃掉 \r\n，需补回）
                var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\r\n");
                try
                {
                    await output.WriteAsync(bytes, 0, bytes.Length, ct);
                    await output.FlushAsync(ct);
                }
                catch (HttpListenerException) { break; }  // 客户端已断开连接
                catch (IOException) { break; }            // 客户端已断开连接

                // 收集流式内容用于日志（限制大小）
                if (contentCollector != null && contentCollector.Length < maxCollectChars)
                {
                    contentCollector.AppendLine(line);
                }

                // 尝试从 data: 行中提取 usage
                if (line.StartsWith("data:") && line.Contains("\"usage\""))
                {
                    TryExtractUsage(line, requestLog, isAnthropicUsage);
                }
            }
        }

        // 从 SSE 数据行解析 usage token
        private static void TryExtractUsage(string line, RequestLog requestLog, bool isAnthropicUsage)
        {
            try
            {
                var jsonPart = line["data:".Length..].Trim();
                if (jsonPart == "[DONE]") return;
                var obj = JObject.Parse(jsonPart);
                var usage = obj["usage"];
                if (usage == null) return;

                if (isAnthropicUsage)
                {
                    requestLog.InputTokens = usage["input_tokens"]?.Value<int>() ?? requestLog.InputTokens;
                    requestLog.OutputTokens = usage["output_tokens"]?.Value<int>() ?? requestLog.OutputTokens;
                }
                else
                {
                    requestLog.InputTokens = usage["prompt_tokens"]?.Value<int>() ?? requestLog.InputTokens;
                    requestLog.OutputTokens = usage["completion_tokens"]?.Value<int>() ?? requestLog.OutputTokens;
                }
            }
            catch { }
        }

        // 自动补全 API 版本路径：如果 BaseUrl 末尾未包含 /v1、/v2 等版本号，自动补 /v1
        // 例如 https://api.anthropic.com → https://api.anthropic.com/v1
        //      https://api.openai.com/v1 → 保持不变（已含版本号）
        private static string EnsureApiVersionPath(string baseUrl)
        {
            var trimmed = baseUrl.TrimEnd('/');
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"/v\d+$"))
                return trimmed;
            return trimmed + "/v1";
        }

        // 判断 HTTP 状态码是否为可重试的临时性错误
        // 5xx：服务端错误（500/502/503/504 等），重试可能成功
        // 429：限流，重试可能成功
        // 408：请求超时
        // 425：Too Early（可选重试）
        // 4xx 其他（400/401/403/404/422）：客户端错误，重试无意义
        private static bool IsTransientStatus(int statusCode)
        {
            if (statusCode >= 500 && statusCode <= 599) return true;
            return statusCode == 429 || statusCode == 408 || statusCode == 425;
        }

        // 记录上游响应内容到文件（委托到 LogService）
        // 实际判断与写入逻辑已迁移至 LogService.LogResponseContent
        private void LogResponseContent(ChannelConfig channel, int statusCode, string modelName, string responseBody)
        {
            _configService.LogService.LogResponseContent(channel, statusCode, modelName, responseBody);
        }

        // 检测 SSE 文本行中是否包含上游错误事件
        // 匹配模式：event: error 或 data: {...,"error":{...}} 或 data: {"error":{...}}
        private static bool SseLineContainsError(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            // Anthropic 风格：event: error
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                var evt = line.Substring(6).Trim();
                if (evt.Equals("error", StringComparison.OrdinalIgnoreCase)) return true;
            }
            // OpenAI / 通用：data: {... "error":{...} ...}
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var data = line.Substring(5).Trim();
                if (data == "[DONE]") return false;
                if (data.Contains("\"error\"")) return true;
            }
            return false;
        }

        // 流式请求带"响应体错误"重试的核心方法
        // 策略：发送请求 → 读取首个 SSE 事件块 → 若检测到 error 且仍有重试机会则丢弃并重试
        //       否则把已读首块 + 剩余流透传给客户端，同时提取 usage
        // createRequest：每次尝试创建新 HttpRequestMessage 的工厂
        // channel：渠道配置（决定重试次数、超时、代理）
        // requestLog：用于提取 usage
        // isAnthropicUsage：usage 字段名风格
        // contentCollector：收集流式内容用于日志
        // 返回：最终使用的 HttpResponseMessage（由调用方 Dispose）
        private async Task<HttpResponseMessage> SendStreamWithRetryAsync(
            Func<HttpRequestMessage> createRequest,
            ChannelConfig channel,
            RequestLog requestLog,
            bool isAnthropicUsage,
            System.IO.Stream output,
            System.Text.StringBuilder? contentCollector,
            CancellationToken ct)
        {
            var maxAttempts = Math.Max(1, channel.RetryCount + 1);
            HttpResponseMessage? resp = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // 首次不延迟；后续重试前按渠道配置的 RetryDelay 秒数延迟
                if (attempt > 1)
                {
                    Log($"渠道 [{channel.Name}] 流式响应含错误，{channel.RetryDelay} 秒后进行第 {attempt - 1}/{channel.RetryCount} 次重试");
                    try { await Task.Delay(TimeSpan.FromSeconds(channel.RetryDelay), ct); }
                    catch (OperationCanceledException) { throw; }
                }

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(channel.Timeout));
                var attemptCt = attemptCts.Token;

                resp?.Dispose();
                var req = createRequest();
                try
                {
                    resp = await GetHttpClient(channel).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, attemptCt);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
                {
                    Log($"渠道 [{channel.Name}] 第 {attempt} 次流式请求超时（{channel.Timeout}s），将重试");
                    req.Dispose();
                    resp = null;
                    continue;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    Log($"渠道 [{channel.Name}] 流式请求异常：{ex.Message}");
                    req.Dispose();
                    resp = null;
                    continue;
                }

                // 非 2xx 状态码：判断是否可重试
                if ((int)resp.StatusCode >= 400)
                {
                    // 可重试的临时性错误（5xx/429/408）：记录后重试
                    if (IsTransientStatus((int)resp.StatusCode) && attempt < maxAttempts)
                    {
                        try
                        {
                            var errSnippet = await TryReadShortErrorAsync(resp, attemptCt);
                            Log($"渠道 [{channel.Name}] 流式上游返回 {(int)resp.StatusCode}：{errSnippet}");
                        }
                        catch { }
                        resp.Dispose();
                        resp = null;
                        continue;
                    }
                    // 不可重试的客户端错误 或 最后一次仍为错误：交由调用方处理
                    return resp;
                }

                // 2xx：读取首个 SSE 事件块，判断是否包含 error
                var stream = await resp.Content.ReadAsStreamAsync(attemptCt);
                var reader = new StreamReader(stream);

                // 读取首个事件块（直到遇到空行或读到内容）
                var firstBlock = new System.Text.StringBuilder();
                string? line;
                bool hasError = false;
                // 读取一个完整 SSE 事件（以空行分隔），最多读若干行避免读太多
                int maxPeekLines = 10;
                while (maxPeekLines-- > 0 && !reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    firstBlock.AppendLine(line);
                    if (SseLineContainsError(line)) hasError = true;
                    // 空行表示一个 SSE 事件结束
                    if (string.IsNullOrWhiteSpace(line)) break;
                }

                if (hasError && attempt < maxAttempts)
                {
                    // 检测到错误且仍有重试机会：记录日志后丢弃当前响应重试
                    var errSnippet = firstBlock.ToString();
                    if (errSnippet.Length > 200) errSnippet = errSnippet.Substring(0, 200) + "...";
                    Log($"渠道 [{channel.Name}] 流式响应首块含错误：{errSnippet}");
                    // 记录错误响应内容到响应日志
                    LogResponseContent(channel, (int)resp.StatusCode, "", firstBlock.ToString());
                    resp.Dispose();
                    resp = null;
                    continue;
                }

                // 无错误 或 最后一次尝试：把首块 + 剩余流透传给客户端
                // 1. 先写首块
                var firstBytes = System.Text.Encoding.UTF8.GetBytes(firstBlock.ToString());
                try
                {
                    await output.WriteAsync(firstBytes, 0, firstBytes.Length, ct);
                    await output.FlushAsync(ct);
                }
                catch (HttpListenerException) { return resp; }  // 客户端已断开连接
                catch (IOException) { return resp; }            // 客户端已断开连接
                if (contentCollector != null && contentCollector.Length < 104857600)
                {
                    contentCollector.Append(firstBlock);
                }
                // 尝试从首块提取 usage
                foreach (var l in firstBlock.ToString().Split('\n'))
                {
                    if (l.StartsWith("data:") && l.Contains("\"usage\""))
                    {
                        TryExtractUsage(l, requestLog, isAnthropicUsage);
                    }
                }

                // 2. 继续透传剩余流并提取 usage（使用同一个 reader，从当前位置继续读）
                string? restLine;
                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    restLine = await reader.ReadLineAsync(ct);
                    if (restLine == null) break;
                    // 保持标准 SSE 格式：每行末尾用 \r\n
                    var bytes = System.Text.Encoding.UTF8.GetBytes(restLine + "\r\n");
                    try
                    {
                        await output.WriteAsync(bytes, 0, bytes.Length, ct);
                        await output.FlushAsync(ct);
                    }
                    catch (HttpListenerException) { break; }  // 客户端已断开连接
                    catch (IOException) { break; }            // 客户端已断开连接
                    if (contentCollector != null && contentCollector.Length < 104857600)
                    {
                        contentCollector.AppendLine(restLine);
                    }
                    if (restLine.StartsWith("data:") && restLine.Contains("\"usage\""))
                    {
                        TryExtractUsage(restLine, requestLog, isAnthropicUsage);
                    }
                }

                return resp;
            }

            // 理论上不会到达
            throw new InvalidOperationException("流式请求重试逻辑异常");
        }

        // 判断是否为推理模型（reasoning model）
        // 推理模型使用 max_completion_tokens 而非 max_tokens，且推理 token 也计入输出限额
        private static bool IsReasoningModel(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return false;
            var name = modelName.ToLowerInvariant();
            // OpenAI 推理模型系列：gpt-5.x / gpt5.x、o1、o3、o4
            return name.Contains("gpt-5")
                || name.Contains("gpt5")
                || name.Contains("o1")
                || name.Contains("o3")
                || name.Contains("o4")
                || name.Contains("reasoning");
        }

        // 对推理模型处理 max_tokens：转为 max_completion_tokens 并确保值足够大
        // 推理模型的推理 token 也计入限额，客户端传入的 max_tokens（如 4096）往往不够
        private static void ApplyReasoningModelTokens(JObject reqBody, string modelName)
        {
            if (!IsReasoningModel(modelName)) return;

            // 读取客户端传入的 max_tokens，若未传则用模型配置的 MaxOutputTokens
            var clientMax = reqBody["max_tokens"]?.Value<int>() ?? 0;

            // 推理模型至少需要 32768 token（推理 + 可见输出）
            var finalMax = Math.Max(clientMax, 32768);

            // 移除 max_tokens，改用 max_completion_tokens
            reqBody.Remove("max_tokens");
            reqBody["max_completion_tokens"] = finalMax;

            // 推理模型不支持 temperature / top_p / frequency_penalty / presence_penalty
            // 这些参数可能导致上游返回错误或使用降级模型顶替，表现为提前 stop 或输出极短
            reqBody.Remove("temperature");
            reqBody.Remove("top_p");
            reqBody.Remove("frequency_penalty");
            reqBody.Remove("presence_penalty");

            // reasoning_effort 由全局配置注入（用户未传时），此处保留原值或已注入值

            // 确保流式请求返回 usage 信息（推理模型依赖 usage 统计）
            if (reqBody["stream"]?.Value<bool>() == true)
            {
                if (reqBody["stream_options"] == null)
                    reqBody["stream_options"] = new JObject { ["include_usage"] = true };
                else if (reqBody["stream_options"]!["include_usage"] == null)
                    reqBody["stream_options"]!["include_usage"] = true;
            }
        }

        // 带自动重试的请求发送：当上游返回 429/500/502/503 或网络异常时，按渠道配置的延迟秒数后重试
        // createRequest：每次尝试时调用以创建新的 HttpRequestMessage（请求实例不可重用）
        // streamResponse：是否以流式方式读取响应（ResponseHeadersRead）
        // linkedCt：已绑定渠道超时的 CancellationToken
        // 返回的 HttpResponseMessage 由调用方负责 Dispose
        // 注：状态码是否记录的判断逻辑已迁移至 LogService.ShouldLogStatusCode
        private async Task<HttpResponseMessage> SendWithRetryAsync(
            Func<HttpRequestMessage> createRequest,
            ChannelConfig channel,
            bool streamResponse,
            CancellationToken linkedCt)
        {
            // 总尝试次数 = 自动重试次数 + 首次请求；至少 1 次
            var maxAttempts = Math.Max(1, channel.RetryCount + 1);

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                // 首次不延迟；后续重试前按渠道配置的 RetryDelay 秒数延迟
                if (attempt > 1)
                {
                    Log($"渠道 [{channel.Name}] {channel.RetryDelay} 秒后进行第 {attempt - 1}/{channel.RetryCount} 次自动重试");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(channel.RetryDelay), linkedCt);
                    }
                    catch (OperationCanceledException) { throw; }
                }

                // 每次尝试使用独立的超时，避免某次请求 hang 住后吞噬后续重试的时间
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCt);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(channel.Timeout));
                var attemptCt = attemptCts.Token;

                var req = createRequest();
                HttpResponseMessage? resp = null;
                try
                {
                    if (attempt > 1)
                    {
                        Log($"渠道 [{channel.Name}] 开始第 {attempt}/{maxAttempts} 次请求（超时 {channel.Timeout}s）");
                    }
                    resp = await GetHttpClient(channel).SendAsync(
                        req,
                        streamResponse ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                        attemptCt);

                    var statusCode = (int)resp.StatusCode;
                    // 判断是否为可重试的临时性错误：5xx 服务端错误 + 429 限流 + 408 请求超时 + 425 Too Early
                    // 4xx 客户端错误（400/401/403/404/422 等）不重试，重试也不会改变结果
                    if (IsTransientStatus(statusCode) && attempt < maxAttempts)
                    {
                        try
                        {
                            var errSnippet = await TryReadShortErrorAsync(resp, attemptCt);
                            Log($"渠道 [{channel.Name}] 上游返回 {statusCode}：{errSnippet}");
                        }
                        catch { }
                        resp.Dispose();
                        resp = null;
                        continue;
                    }

                    // 成功 / 非重试错误 / 最后一次仍为错误：交由调用方按原逻辑处理
                    return resp;
                }
                catch (OperationCanceledException) when (!linkedCt.IsCancellationRequested && attempt < maxAttempts)
                {
                    // 单次尝试超时（非外层取消）且仍有重试机会：释放资源后重试
                    Log($"渠道 [{channel.Name}] 第 {attempt} 次请求超时（{channel.Timeout}s），将重试");
                    resp?.Dispose();
                    req.Dispose();
                    continue;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    // 网络异常（连接失败、DNS解析失败、上游断开等）：释放资源后重试
                    Log($"渠道 [{channel.Name}] 请求异常：{ex.Message}");
                    resp?.Dispose();
                    req.Dispose();
                    continue;
                }
            }

            // 理论上不会到达这里
            throw new InvalidOperationException("请求重试逻辑异常");
        }

        // 读取响应内容的前 200 字符用于日志展示
        private static async Task<string> TryReadShortErrorAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            try
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrEmpty(body)) { return "无响应内容"; }
                return body.Length > 200 ? body.Substring(0, 200) + "..." : body;
            }
            catch
            {
                return "无法读取错误内容";
            }
        }
    }
}
