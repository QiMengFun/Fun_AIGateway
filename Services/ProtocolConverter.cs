using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FunAiGateway.Models;

namespace FunAiGateway.Services
{
    /// <summary>
    /// OpenAI与Anthropic协议互转核心
    /// </summary>
    public static class ProtocolConverter
    {
        // OpenAI请求转Anthropic请求
        public static (JObject anthropicRequest, string? systemPrompt) OpenAIToAnthropic(JObject openaiReq)
        {
            var anthropicReq = new JObject();

            // model
            if (openaiReq["model"] != null)
                anthropicReq["model"] = openaiReq["model"];

            // max_tokens
            // 推理模型客户端可能用 max_completion_tokens 而非 max_tokens
            var maxTokens = openaiReq["max_tokens"]?.Value<int>()
                ?? openaiReq["max_completion_tokens"]?.Value<int>()
                ?? 4096;
            // 推理模型的推理 token 也计入限额，确保至少 32768
            var modelName = openaiReq["model"]?.ToString() ?? "";
            if (IsReasoningModelName(modelName) && maxTokens < 32768)
                maxTokens = 32768;
            anthropicReq["max_tokens"] = maxTokens;

            // temperature
            if (openaiReq["temperature"] != null)
                anthropicReq["temperature"] = openaiReq["temperature"];

            // top_p
            if (openaiReq["top_p"] != null)
                anthropicReq["top_p"] = openaiReq["top_p"];

            // stop
            if (openaiReq["stop"] != null)
                anthropicReq["stop_sequences"] = openaiReq["stop"];

            // stream
            if (openaiReq["stream"] != null)
                anthropicReq["stream"] = openaiReq["stream"];

            // messages转换 - 提取system
            string? systemPrompt = null;
            var messages = openaiReq["messages"] as JArray ?? new JArray();
            var anthropicMessages = new JArray();

            foreach (var msg in messages)
            {
                var role = msg["role"]?.ToString();
                var content = msg["content"];

                if (role == "system")
                {
                    systemPrompt = content?.ToString();
                    continue;
                }

                if (role == "assistant")
                {
                    // 检查是否有tool_calls
                    var toolCalls = msg["tool_calls"] as JArray;
                    if (toolCalls != null && toolCalls.Count > 0)
                    {
                        var contentBlocks = new JArray();
                        // 先添加文本内容（如果有）
                        if (content != null && content.Type != JTokenType.Null)
                        {
                            var textStr = content.Type == JTokenType.String ? content.ToString() : content.ToString(Formatting.None);
                            if (!string.IsNullOrWhiteSpace(textStr))
                                contentBlocks.Add(new JObject { ["type"] = "text", ["text"] = textStr });
                        }
                        foreach (var tc in toolCalls)
                        {
                            contentBlocks.Add(new JObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = tc["id"]?.ToString() ?? Guid.NewGuid().ToString(),
                                ["name"] = tc["function"]?["name"]?.ToString() ?? "",
                                ["input"] = tc["function"]?["arguments"] != null
                                    ? JObject.Parse(tc["function"]!["arguments"]!.ToString())
                                    : new JObject()
                            });
                        }
                        anthropicMessages.Add(new JObject { ["role"] = "assistant", ["content"] = contentBlocks });
                    }
                    else
                    {
                        anthropicMessages.Add(new JObject { ["role"] = "assistant", ["content"] = content ?? "" });
                    }
                    continue;
                }

                if (role == "tool")
                {
                    // tool结果
                    var toolId = msg["tool_call_id"]?.ToString() ?? "";
                    anthropicMessages.Add(new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = toolId,
                                ["content"] = content?.ToString() ?? ""
                            }
                        }
                    });
                    continue;
                }

                if (role == "user")
                {
                    // 处理多模态内容
                    if (content is JArray contentArray)
                    {
                        var converted = new JArray();
                        foreach (var item in contentArray)
                        {
                            var type = item["type"]?.ToString();
                            if (type == "text")
                                converted.Add(new JObject { ["type"] = "text", ["text"] = item["text"]?.ToString() ?? "" });
                            else if (type == "image_url")
                            {
                                var imageUrl = item["image_url"]?["url"]?.ToString() ?? "";
                                if (imageUrl.StartsWith("data:"))
                                {
                                    var parts = imageUrl.Split(',', 2);
                                    var mediaType = parts[0].Replace("data:", "").Replace(";base64", "");
                                    converted.Add(new JObject
                                    {
                                        ["type"] = "image",
                                        ["source"] = new JObject
                                        {
                                            ["type"] = "base64",
                                            ["media_type"] = mediaType,
                                            ["data"] = parts.Length > 1 ? parts[1] : ""
                                        }
                                    });
                                }
                                else
                                {
                                    converted.Add(new JObject
                                    {
                                        ["type"] = "image",
                                        ["source"] = new JObject
                                        {
                                            ["type"] = "url",
                                            ["url"] = imageUrl
                                        }
                                    });
                                }
                            }
                        }
                        anthropicMessages.Add(new JObject { ["role"] = "user", ["content"] = converted });
                    }
                    else
                    {
                        anthropicMessages.Add(new JObject { ["role"] = "user", ["content"] = content?.ToString() ?? "" });
                    }
                    continue;
                }

                // 其他role直接传递
                anthropicMessages.Add(msg);
            }

            anthropicReq["messages"] = anthropicMessages;

            // system
            if (systemPrompt != null)
                anthropicReq["system"] = systemPrompt;

            // tools转换
            var openaiTools = openaiReq["tools"] as JArray;
            if (openaiTools != null && openaiTools.Count > 0)
            {
                var anthropicTools = new JArray();
                foreach (var tool in openaiTools)
                {
                    if (tool["type"]?.ToString() == "function")
                    {
                        var func = tool["function"];
                        anthropicTools.Add(new JObject
                        {
                            ["name"] = func?["name"]?.ToString() ?? "",
                            ["description"] = func?["description"]?.ToString() ?? "",
                            ["input_schema"] = func?["parameters"] ?? new JObject { ["type"] = "object" }
                        });
                    }
                }
                if (anthropicTools.Count > 0)
                    anthropicReq["tools"] = anthropicTools;
            }

            return (anthropicReq, systemPrompt);
        }

        // Anthropic响应转OpenAI响应
        public static JObject AnthropicToOpenAIResponse(JObject anthropicResp, string modelName, bool stream = false)
        {
            if (stream)
            {
                // 流式响应由单独方法处理
                return anthropicResp;
            }

            var openaiResp = new JObject
            {
                ["id"] = anthropicResp["id"]?.ToString() ?? $"chatcmpl-{Guid.NewGuid():N}",
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = modelName,
                ["choices"] = new JArray(),
                ["usage"] = new JObject()
            };

            var content = anthropicResp["content"] as JArray ?? new JArray();
            var stopReason = anthropicResp["stop_reason"]?.ToString();

            var message = new JObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = null as JToken
            };

            var textParts = new List<string>();
            var toolCalls = new JArray();

            foreach (var block in content)
            {
                var type = block["type"]?.ToString();
                if (type == "text")
                    textParts.Add(block["text"]?.ToString() ?? "");
                else if (type == "tool_use")
                {
                    toolCalls.Add(new JObject
                    {
                        ["id"] = block["id"]?.ToString() ?? "",
                        ["type"] = "function",
                        ["function"] = new JObject
                        {
                            ["name"] = block["name"]?.ToString() ?? "",
                            ["arguments"] = block["input"]?.ToString(Formatting.None) ?? "{}"
                        }
                    });
                }
            }

            message["content"] = string.Join("", textParts);
            if (toolCalls.Count > 0)
                message["tool_calls"] = toolCalls;

            var finishReason = stopReason switch
            {
                "end_turn" => "stop",
                "max_tokens" => "length",
                "stop_sequence" => "stop",
                "tool_use" => "tool_calls",
                _ => "stop"
            };

            (openaiResp["choices"] as JArray)!.Add(new JObject
            {
                ["index"] = 0,
                ["message"] = message,
                ["finish_reason"] = finishReason
            });

            // usage
            var usage = (openaiResp["usage"] as JObject)!;
            usage["prompt_tokens"] = anthropicResp["usage"]?["input_tokens"]?.Value<int>() ?? 0;
            usage["completion_tokens"] = anthropicResp["usage"]?["output_tokens"]?.Value<int>() ?? 0;
            var pt = usage["prompt_tokens"]?.Value<int>() ?? 0;
            var ct2 = usage["completion_tokens"]?.Value<int>() ?? 0;
            usage["total_tokens"] = pt + ct2;

            return openaiResp;
        }

        // Anthropic流式事件转OpenAI流式格式
        public static string? ConvertAnthropicStreamEvent(string line, string modelName, string requestId)
        {
            try
            {
                if (!line.StartsWith("data: ")) return null;
                var data = line.Substring(6).Trim();
                if (data == "[DONE]") return "data: [DONE]\r\n\r\n";

                var evt = JObject.Parse(data);
                var eventType = evt["type"]?.ToString();

                switch (eventType)
                {
                    case "message_start":
                        return $"data: {JsonConvert.SerializeObject(new JObject
                        {
                            ["id"] = requestId,
                            ["object"] = "chat.completion.chunk",
                            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["model"] = modelName,
                            ["choices"] = new JArray { new JObject
                            {
                                ["index"] = 0,
                                ["delta"] = new JObject { ["role"] = "assistant", ["content"] = "" },
                                ["finish_reason"] = null as JToken
                            } }
                        })}\r\n\r\n";

                    case "content_block_start":
                        var block = evt["content_block"] as JObject;
                        if (block?["type"]?.ToString() == "tool_use")
                        {
                            return $"data: {JsonConvert.SerializeObject(new JObject
                            {
                                ["id"] = requestId,
                                ["object"] = "chat.completion.chunk",
                                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                ["model"] = modelName,
                                ["choices"] = new JArray { new JObject
                                {
                                    ["index"] = 0,
                                    ["delta"] = new JObject
                                    {
                                        ["tool_calls"] = new JArray { new JObject
                                        {
                                            ["index"] = evt["index"]?.Value<int>() ?? 0,
                                            ["id"] = block["id"]?.ToString() ?? "",
                                            ["type"] = "function",
                                            ["function"] = new JObject
                                            {
                                                ["name"] = block["name"]?.ToString() ?? "",
                                                ["arguments"] = ""
                                            }
                                        } }
                                    },
                                    ["finish_reason"] = null as JToken
                                } }
                            })}\r\n\r\n";
                        }
                        return null; // text block start不需要特殊处理

                    case "content_block_delta":
                        var delta = evt["delta"] as JObject;
                        if (delta?["type"]?.ToString() == "text_delta")
                        {
                            return $"data: {JsonConvert.SerializeObject(new JObject
                            {
                                ["id"] = requestId,
                                ["object"] = "chat.completion.chunk",
                                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                ["model"] = modelName,
                                ["choices"] = new JArray { new JObject
                                {
                                    ["index"] = 0,
                                    ["delta"] = new JObject { ["content"] = delta["text"]?.ToString() ?? "" },
                                    ["finish_reason"] = null as JToken
                                } }
                            })}\r\n\r\n";
                        }
                        if (delta?["type"]?.ToString() == "input_json_delta")
                        {
                            return $"data: {JsonConvert.SerializeObject(new JObject
                            {
                                ["id"] = requestId,
                                ["object"] = "chat.completion.chunk",
                                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                ["model"] = modelName,
                                ["choices"] = new JArray { new JObject
                                {
                                    ["index"] = 0,
                                    ["delta"] = new JObject
                                    {
                                        ["tool_calls"] = new JArray { new JObject
                                        {
                                            ["index"] = evt["index"]?.Value<int>() ?? 0,
                                            ["function"] = new JObject { ["arguments"] = delta["partial_json"]?.ToString() ?? "" }
                                        } }
                                    },
                                    ["finish_reason"] = null as JToken
                                } }
                            })}\r\n\r\n";
                        }
                        return null;

                    case "message_delta":
                        var stopReason = evt["delta"]?["stop_reason"]?.ToString();
                        var finishReason = stopReason switch
                        {
                            "end_turn" => "stop",
                            "max_tokens" => "length",
                            "tool_use" => "tool_calls",
                            _ => stopReason
                        };
                        return $"data: {JsonConvert.SerializeObject(new JObject
                        {
                            ["id"] = requestId,
                            ["object"] = "chat.completion.chunk",
                            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["model"] = modelName,
                            ["choices"] = new JArray { new JObject
                            {
                                ["index"] = 0,
                                ["delta"] = new JObject(),
                                ["finish_reason"] = finishReason
                            } }
                        })}\r\n\r\n";

                    case "message_stop":
                        return "data: [DONE]\r\n\r\n";

                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        // 判断是否为推理模型（reasoning model）
        private static bool IsReasoningModelName(string modelName)
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
    }

    /// <summary>
    /// OpenAI 流式响应 → Anthropic 流式的有状态转换器
    /// 完整支持文本内容、tool_calls、finish_reason 映射与 usage 统计
    /// 每个 SSE 流对应一个实例，维护 block 顺序与开关状态
    /// </summary>
    public class OpenAIToAnthropicStreamConverter
    {
        private int _nextBlockIndex = 0;
        private int _textBlockIndex = -1;   // -1 表示文本 block 尚未创建
        private bool _textBlockOpen = false;
        // OpenAI 的 tool_calls[].index → Anthropic 的 content block index 映射
        private readonly Dictionary<int, int> _toolIdxToBlockIdx = new();
        private readonly HashSet<int> _openToolBlocks = new();
        private bool _finished = false;       // 是否已输出 message_delta（finish_reason）
        private int _inputTokens = 0;
        private int _outputTokens = 0;

        // 处理一行 OpenAI SSE，返回需要写给客户端的 Anthropic SSE 文本（可能为 null）
        public string? ConvertLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            if (!line.StartsWith("data: ")) return null;
            var data = line.Substring(6).Trim();
            if (data == "[DONE]") return null; // message_stop 由调用方处理

            try
            {
                var chunk = JObject.Parse(data);

                // usage 可能与 finish_reason 同块出现，也可能单独出现
                if (chunk["usage"] is JObject usageObj)
                {
                    _inputTokens = usageObj["prompt_tokens"]?.Value<int>() ?? 0;
                    _outputTokens = usageObj["completion_tokens"]?.Value<int>() ?? 0;
                }

                var sb = new System.Text.StringBuilder();
                var choice = chunk["choices"]?[0] as JObject;
                if (choice == null)
                {
                    // 无 choice（如纯 usage chunk），无需写内容事件
                    return sb.Length > 0 ? sb.ToString() : null;
                }

                var delta = choice["delta"] as JObject;
                var finishReason = choice["finish_reason"]?.ToString();

                // 1) 文本内容 delta（OpenAI 通常先发文本，再发 tool_calls）
                if (delta?["content"] != null && delta["content"].Type != JTokenType.Null)
                {
                    var text = delta["content"].ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        // 文本 block 尚未创建，懒创建
                        if (!_textBlockOpen && _textBlockIndex < 0)
                        {
                            _textBlockIndex = _nextBlockIndex++;
                            _textBlockOpen = true;
                            sb.Append(EmitContentBlockStartText(_textBlockIndex));
                        }
                        if (_textBlockOpen)
                            sb.Append(EmitContentBlockDeltaText(_textBlockIndex, text));
                    }
                }

                // 2) tool_calls delta
                if (delta?["tool_calls"] is JArray toolCalls)
                {
                    // 关闭可能已打开的文本 block（Anthropic 不允许同 block 内混合 text 与 tool_use）
                    if (_textBlockOpen)
                    {
                        sb.Append(EmitContentBlockStop(_textBlockIndex));
                        _textBlockOpen = false;
                    }

                    foreach (var tc in toolCalls)
                    {
                        var oaiIdx = tc["index"]?.Value<int>() ?? 0;
                        var args = tc["function"]?["arguments"]?.ToString();

                        if (!_toolIdxToBlockIdx.ContainsKey(oaiIdx))
                        {
                            // 新的 tool block
                            var blockIdx = _nextBlockIndex++;
                            _toolIdxToBlockIdx[oaiIdx] = blockIdx;
                            _openToolBlocks.Add(blockIdx);
                            var id = tc["id"]?.ToString() ?? $"toolu_{Guid.NewGuid():N}";
                            var name = tc["function"]?["name"]?.ToString() ?? "";
                            sb.Append(EmitContentBlockStartToolUse(blockIdx, id, name));
                            if (!string.IsNullOrEmpty(args))
                                sb.Append(EmitContentBlockDeltaInputJson(blockIdx, args));
                        }
                        else
                        {
                            // 已存在的 tool block：仅追加 arguments 增量
                            var blockIdx = _toolIdxToBlockIdx[oaiIdx];
                            if (!string.IsNullOrEmpty(args))
                                sb.Append(EmitContentBlockDeltaInputJson(blockIdx, args));
                        }
                    }
                }

                // 3) finish_reason：关闭所有打开的 block，输出 message_delta
                if (!string.IsNullOrEmpty(finishReason))
                {
                    sb.Append(CloseAllBlocks());
                    sb.Append(EmitMessageDelta(MapStopReason(finishReason), _outputTokens));
                    _finished = true;
                }

                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
        }

        // 流结束时若未收到 finish_reason，补足 block_stop 与 message_delta
        public string? EmitTail()
        {
            if (_finished) return null;
            var sb = new System.Text.StringBuilder();
            sb.Append(CloseAllBlocks());
            sb.Append(EmitMessageDelta("end_turn", _outputTokens));
            _finished = true;
            return sb.ToString();
        }

        // 关闭所有打开的 block，返回需要写出的内容
        private string CloseAllBlocks()
        {
            var sb = new System.Text.StringBuilder();
            if (_textBlockOpen)
            {
                sb.Append(EmitContentBlockStop(_textBlockIndex));
                _textBlockOpen = false;
            }
            if (_openToolBlocks.Count > 0)
            {
                foreach (var blockIdx in _openToolBlocks)
                    sb.Append(EmitContentBlockStop(blockIdx));
                _openToolBlocks.Clear();
            }
            return sb.ToString();
        }

        public bool Finished => _finished;
        public int InputTokens => _inputTokens;
        public int OutputTokens => _outputTokens;

        // finish_reason → stop_reason 映射
        private static string MapStopReason(string finishReason) => finishReason switch
        {
            "stop" => "end_turn",
            "length" => "max_tokens",
            "tool_calls" => "tool_use",
            _ => "end_turn"
        };

        private static string EmitContentBlockStartText(int idx) =>
            $"event: content_block_start\ndata: {JsonConvert.SerializeObject(new JObject { ["type"] = "content_block_start", ["index"] = idx, ["content_block"] = new JObject { ["type"] = "text", ["text"] = "" } })}\r\n\r\n";

        private static string EmitContentBlockStartToolUse(int idx, string id, string name) =>
            $"event: content_block_start\ndata: {JsonConvert.SerializeObject(new JObject { ["type"] = "content_block_start", ["index"] = idx, ["content_block"] = new JObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JObject() } })}\r\n\r\n";

        private static string EmitContentBlockDeltaText(int idx, string text) =>
            $"event: content_block_delta\ndata: {JsonConvert.SerializeObject(new JObject { ["type"] = "content_block_delta", ["index"] = idx, ["delta"] = new JObject { ["type"] = "text_delta", ["text"] = text } })}\r\n\r\n";

        private static string EmitContentBlockDeltaInputJson(int idx, string partial) =>
            $"event: content_block_delta\ndata: {JsonConvert.SerializeObject(new JObject { ["type"] = "content_block_delta", ["index"] = idx, ["delta"] = new JObject { ["type"] = "input_json_delta", ["partial_json"] = partial } })}\r\n\r\n";

        private static string EmitContentBlockStop(int idx) =>
            $"event: content_block_stop\ndata: {JsonConvert.SerializeObject(new JObject { ["type"] = "content_block_stop", ["index"] = idx })}\r\n\r\n";

        private static string EmitMessageDelta(string stopReason, int outputTokens) =>
            $"event: message_delta\ndata: {JsonConvert.SerializeObject(new JObject { ["type"] = "message_delta", ["delta"] = new JObject { ["stop_reason"] = stopReason }, ["usage"] = new JObject { ["output_tokens"] = outputTokens } })}\r\n\r\n";
    }
}
