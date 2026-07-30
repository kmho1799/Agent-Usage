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

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken)
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
                return UsageSnapshot.Unavailable(plan, "Claude Code에서 /usage를 실행해 로그인을 갱신하세요.");
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
                return UsageSnapshot.Unavailable(plan, "Claude Code에서 /usage를 실행해 로그인을 갱신하세요.");
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
