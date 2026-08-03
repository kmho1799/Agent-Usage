using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TokenPulse.Models;

namespace TokenPulse.Services;

public sealed class ClaudeUsageService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude",
        ".credentials.json");

    public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken) =>
        GetUsageAsync(cancellationToken, refreshExpiredToken: true);

    private async Task<UsageSnapshot> GetUsageAsync(
        CancellationToken cancellationToken,
        bool refreshExpiredToken)
    {
        try
        {
            if (!File.Exists(CredentialsPath))
            {
                return UsageSnapshot.Unavailable("Claude", "Claude Code 로그인이 필요합니다.");
            }

            using var credentialsDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(CredentialsPath, cancellationToken));

            if (!credentialsDocument.RootElement.TryGetProperty("claudeAiOauth", out var oauth) ||
                !oauth.TryGetProperty("accessToken", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String)
            {
                return UsageSnapshot.Unavailable("Claude", "Claude Code OAuth 로그인을 찾지 못했습니다.");
            }

            var plan = ReadPlan(oauth);
            if (IsExpired(oauth))
            {
                if (refreshExpiredToken && await RefreshCredentialsAsync(cancellationToken))
                {
                    return await GetUsageAsync(cancellationToken, refreshExpiredToken: false);
                }

                return UsageSnapshot.Unavailable(plan, "Claude CLI를 실행해주세요.");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenElement.GetString());
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("token-pulse/1.0.0");

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return UsageSnapshot.Unavailable(plan, "Claude CLI를 실행해주세요.");
            }

            response.EnsureSuccessStatusCode();
            using var usageDocument = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));

            var session = ParseBucket(usageDocument.RootElement, "five_hour");
            var weekly = ParseBucket(usageDocument.RootElement, "seven_day");

            return session is null
                ? UsageSnapshot.Unavailable(plan, "Claude 세션 한도를 찾지 못했습니다.")
                : new UsageSnapshot(plan, session, weekly);
        }
        catch (OperationCanceledException)
        {
            return UsageSnapshot.Unavailable("Claude", "Claude 응답 시간이 초과되었습니다.");
        }
        catch (HttpRequestException ex)
        {
            return UsageSnapshot.Unavailable("Claude", $"Claude 연결 실패: {ex.Message}");
        }
        catch (JsonException)
        {
            return UsageSnapshot.Unavailable("Claude", "Claude 사용량 응답을 읽지 못했습니다.");
        }
        catch (Exception ex)
        {
            return UsageSnapshot.Unavailable("Claude", $"Claude 확인 실패: {ex.Message}");
        }
    }

    private static async Task<bool> RefreshCredentialsAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var startInfo = new ProcessStartInfo
        {
            FileName = "claude",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("OK라고만 답하세요.");
        startInfo.ArgumentList.Add("--tools");
        startInfo.ArgumentList.Add("");
        startInfo.ArgumentList.Add("--max-turns");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--no-session-persistence");

        using var process = new Process { StartInfo = startInfo };
        var started = false;
        try
        {
            started = process.Start();
            if (!started)
            {
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(outputTask, errorTask);
            return process.ExitCode == 0;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static string ReadPlan(JsonElement oauth)
    {
        if (oauth.TryGetProperty("subscriptionType", out var subscription) &&
            subscription.ValueKind == JsonValueKind.String)
        {
            var value = subscription.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return $"Claude {char.ToUpperInvariant(value[0])}{value[1..]}";
            }
        }

        return "Claude";
    }

    private static bool IsExpired(JsonElement oauth)
    {
        if (!oauth.TryGetProperty("expiresAt", out var expiresAt) ||
            expiresAt.ValueKind != JsonValueKind.Number ||
            !expiresAt.TryGetInt64(out var unixMilliseconds))
        {
            return false;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds) <= DateTimeOffset.UtcNow;
    }

    private static UsageBucket? ParseBucket(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var bucket) ||
            bucket.ValueKind != JsonValueKind.Object ||
            !bucket.TryGetProperty("utilization", out var utilizationElement) ||
            !utilizationElement.TryGetDouble(out var utilization))
        {
            return null;
        }

        DateTimeOffset? resetsAt = null;
        if (bucket.TryGetProperty("resets_at", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetElement.GetString(), out var parsedReset))
        {
            resetsAt = parsedReset;
        }

        return new UsageBucket(Math.Clamp(utilization, 0, 100), resetsAt);
    }
}
