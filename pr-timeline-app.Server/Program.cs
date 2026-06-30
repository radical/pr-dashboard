var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.AddKeyedAzureBlobContainerClient(GitHubPublicCacheStore.ConnectionName);
builder.AddKeyedAzureBlobContainerClient(BlobNotificationStore.ConnectionName);
builder.Services.Configure<GitHubCacheWarmupOptions>(
    builder.Configuration.GetSection(GitHubCacheWarmupOptions.SectionName));
builder.Services.Configure<WebPushOptions>(
    builder.Configuration.GetSection(WebPushOptions.SectionName));
builder.Services.Configure<GitHubReviewPolicyOptions>(
    builder.Configuration.GetSection(GitHubReviewPolicyOptions.SectionName));
builder.Services.Configure<CiHealthOptions>(
    builder.Configuration.GetSection(CiHealthOptions.SectionName));
builder.Services.AddGitHubApiServices(builder.Environment);
builder.Services.AddNotificationServices();
builder.Services.AddCiHealthServices();

var app = builder.Build();

app.UseGitHubApiExceptionHandler();
app.UseAuthentication();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGitHubAuthRoutes();
app.MapGitHubPullRequestRoutes();
app.MapNotificationRoutes();
app.MapCiHealthRoutes();
app.MapGet("/api/app-info", (IConfiguration configuration) =>
{
    var commitSha = configuration["GIT_COMMIT_SHA"]?.Trim() is { Length: > 0 } configuredCommitSha
        ? configuredCommitSha
        : "local";
    var shortCommitSha = commitSha[..Math.Min(7, commitSha.Length)];
    var commitUrl = commitSha == "local"
        ? null
        : $"https://github.com/davidfowl/pr-dashboard/commit/{commitSha}";

    return new AppInfoResponse(commitSha, shortCommitSha, commitUrl);
});

app.MapDefaultEndpoints();

app.UseFileServer();

app.Run();

public partial class Program;

record AppInfoResponse(string CommitSha, string ShortCommitSha, string? CommitUrl);
