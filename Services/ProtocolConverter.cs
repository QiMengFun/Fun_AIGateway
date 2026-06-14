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
            var maxTokens = openaiReq["max_tokens"]?.Value<int>() ?? 4096;
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
                if (data == "[DONE]") return "data: [DONE]\n\n";

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
                        })}\n\n";

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
                            })}\n\n";
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
                            })}\n\n";
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
                            })}\n\n";
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
                        })}\n\n";

                    case "message_stop":
                        return "data: [DONE]\n\n";

                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        // OpenAI流式事件转Anthropic流式格式
        public static string? ConvertOpenAIStreamToAnthropic(string line, string model, string? systemPrompt)
        {
            // 这个方向比较少用，但提供基本支持
            try
            {
                if (!line.StartsWith("data: ")) return null;
                var data = line.Substring(6).Trim();
                if (data == "[DONE]") return "event: message_stop\ndata: {}\n\n";

                var chunk = JObject.Parse(data);
                var delta = chunk["choices"]?[0]?["delta"] as JObject;
                if (delta == null) return null;

                // 简化处理：只转换文本内容
                var content = delta["content"]?.ToString();
                if (content != null)
                {
                    return $"event: content_block_delta\ndata: {JsonConvert.SerializeObject(new JObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = 0,
                        ["delta"] = new JObject { ["type"] = "text_delta", ["text"] = content }
                    })}\n\n";
                }

                return null;
            }
            catch { return null; }
        }
    }
}
