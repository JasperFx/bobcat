using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Bobcat.Monitor.Coordination.GitHub;

/// <summary>
/// The real <see cref="IGitHubQueryClient"/>: GitHub's GraphQL endpoint with a bearer token
/// resolved from <c>Monitor:GitHubToken</c> configuration, then <c>GITHUB_TOKEN</c>, then
/// <c>GH_TOKEN</c>. Null token means "not configured" — the polling service handles that.
/// </summary>
public sealed class GitHubQueryClient : IGitHubQueryClient
{
    public const string TokenVariable = "GITHUB_TOKEN";
    public const string GhCliTokenVariable = "GH_TOKEN";

    private readonly HttpClient _http;

    public GitHubQueryClient(HttpClient http, string token)
    {
        _http = http;
        _http.BaseAddress ??= new Uri("https://api.github.com/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("bobcat-monitor");
    }

    public static string? ResolveToken(IConfiguration configuration)
        => configuration["Monitor:GitHubToken"]
           ?? Environment.GetEnvironmentVariable(TokenVariable)
           ?? Environment.GetEnvironmentVariable(GhCliTokenVariable);

    public async Task<string> PostQueryAsync(string query, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { query });
        using var response = await _http.PostAsync(
            "graphql", new StringContent(body, Encoding.UTF8, "application/json"), ct);

        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub GraphQL returned {(int)response.StatusCode}: {payload}");
        }

        return payload;
    }
}

/// <summary>
/// Periodic sweeps, default 60s (<c>Monitor:GitHubPollSeconds</c>). Without a token the
/// service logs one warning and idles — the same grace as the publisher's ping probe: a
/// monitor with no GitHub access still does everything else, and node statuses render as
/// unknown rather than wrong.
/// </summary>
public class GitHubPollingService : BackgroundService
{
    private readonly GitHubPoller? _poller;
    private readonly TimeSpan _interval;
    private readonly ILogger<GitHubPollingService> _logger;

    public GitHubPollingService(IServiceProvider services, IConfiguration configuration, ILogger<GitHubPollingService> logger)
    {
        _logger = logger;
        _interval = TimeSpan.FromSeconds(configuration.GetValue("Monitor:GitHubPollSeconds", 60d));
        _poller = services.GetService<GitHubPoller>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_poller is null)
        {
            _logger.LogWarning(
                "No GitHub token found ({Config}, {Env}, or {GhEnv}) — plan nodes will render with unknown GitHub status",
                "Monitor:GitHubToken", GitHubQueryClient.TokenVariable, GitHubQueryClient.GhCliTokenVariable);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var changed = await _poller.SweepAsync(stoppingToken);
                if (changed > 0) _logger.LogInformation("GitHub sweep observed {Count} change(s)", changed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "GitHub sweep failed");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
