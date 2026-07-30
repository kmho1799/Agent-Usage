using System.Diagnostics;
using System.Text.Json;
using TokenPulse.Models;

namespace TokenPulse.Services;

public sealed class CodexUsageService
{
    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /s /c \"codex app-server --listen stdio://\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return UsageSnapshot.Unavailable("ChatGPT", "Codex CLI를 시작할 수 없습니다.");
            }

            await SendAsync(process, new
            {
                method = "initialize",
                id = 1,
                @params = new
                {
                    clientInfo = new
                    {
                        name = "agent_usage",
                        title = "Agent Usage",
                        version = "1.0.0"
                    }
                }
            });

            await ReadResponseAsync(process, 1, timeout.Token);
            await SendAsync(process, new { method = "initialized", @params = new { } });
            await SendAsync(process, new { method = "account/read", id = 2, @params = new { refreshToken = false } });
            var account = await ReadResponseAsync(process, 2, timeout.Token);

            await SendAsync(process, new { method = "account/rateLimits/read", id = 3 });
            var rateLimits = await ReadResponseAsync(process, 3, timeout.Token);
            var plan = ReadPlan(account);

            return ParseUsage(rateLimits, plan);
        }
        catch (OperationCanceledException)
        {
            return UsageSnapshot.Unavailable("ChatGPT", "Codex 응답 시간이 초과되었습니다.");
        }
        catch (Exception ex)
        {
            return UsageSnapshot.Unavailable("ChatGPT", $"Codex 연결 실패: {ex.Message}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task SendAsync(Process process, object message)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message));
        await process.StandardInput.FlushAsync();
    }

    private static async Task<JsonElement> ReadResponseAsync(
        Process process,
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error) ? "Codex app-server가 종료되었습니다." : error.Trim());
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number ||
                id.GetInt32() != expectedId)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var errorElement))
            {
                throw new InvalidOperationException(errorElement.ToString());
            }

            return root.Clone();
        }
    }

    private static string ReadPlan(JsonElement response)
    {
        if (response.TryGetProperty("result", out var result) &&
            result.TryGetProperty("account", out var account) &&
            account.ValueKind == JsonValueKind.Object &&
            account.TryGetProperty("planType", out var planType) &&
            planType.ValueKind == JsonValueKind.String)
        {
            var plan = planType.GetString();
            return string.IsNullOrWhiteSpace(plan)
                ? "ChatGPT"
                : $"ChatGPT {char.ToUpperInvariant(plan[0])}{plan[1..]}";
        }

        return "ChatGPT";
    }

    private static UsageSnapshot ParseUsage(JsonElement response, string plan)
    {
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("rateLimits", out var rateLimits) ||
            rateLimits.ValueKind != JsonValueKind.Object)
        {
            return UsageSnapshot.Unavailable(plan, "Codex 사용량 정보가 없습니다.");
        }

        var weekly = ParseBucket(rateLimits, "primary");
        if (weekly is null)
        {
            return UsageSnapshot.Unavailable(plan, "Codex 사용 한도를 찾지 못했습니다.");
        }

        return new UsageSnapshot(plan, weekly, null)
        {
            PrimaryLabel = "주간 남은 사용량"
        };
    }

    private static UsageBucket? ParseBucket(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var bucket) ||
            bucket.ValueKind != JsonValueKind.Object ||
            !bucket.TryGetProperty("usedPercent", out var usedElement) ||
            !usedElement.TryGetDouble(out var usedPercent))
        {
            return null;
        }

        DateTimeOffset? resetsAt = null;
        if (bucket.TryGetProperty("resetsAt", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.Number &&
            resetElement.TryGetInt64(out var resetUnix))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
        }

        return new UsageBucket(Math.Clamp(usedPercent, 0, 100), resetsAt);
    }
}
