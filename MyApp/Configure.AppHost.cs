﻿using Amazon.S3;
using Microsoft.AspNetCore.Mvc.Rendering;
using MyApp.Data;
using ServiceStack.IO;
using ServiceStack.OrmLite;

[assembly: HostingStartup(typeof(MyApp.AppHost))]

namespace MyApp;

public class AppHost() : AppHostBase("MyApp"), IHostingStartup
{
    public void Configure(IWebHostBuilder builder) => builder
        .ConfigureServices((context,services) => {
            // Configure ASP.NET Core IOC Dependencies
            context.Configuration.GetSection(nameof(AppConfig)).Bind(AppConfig.Instance);
            services.AddSingleton(AppConfig.Instance);
            
            var r2AccountId = context.Configuration.GetValue("R2AccountId", Environment.GetEnvironmentVariable("R2_ACCOUNT_ID"));
            var r2AccessId = context.Configuration.GetValue("R2AccessKeyId", Environment.GetEnvironmentVariable("R2_ACCESS_KEY_ID"));
            var r2AccessKey = context.Configuration.GetValue("R2SecretAccessKey", Environment.GetEnvironmentVariable("R2_SECRET_ACCESS_KEY"));
            var s3Client = new AmazonS3Client(r2AccessId, r2AccessKey, new AmazonS3Config
            {
                ServiceURL = $"https://{r2AccountId}.r2.cloudflarestorage.com"
            });
            services.AddSingleton(s3Client);
            services.AddSingleton(new R2VirtualFiles(s3Client, "stackoverflow-shootout"));
        });

    public override void Configure()
    {
        AppConfig.Instance.GitPagesBaseUrl ??= ResolveGitBlobBaseUrl(ContentRootDirectory);
        
        FileSystemVirtualFiles.AssertDirectory(HostingEnvironment.ContentRootPath.CombineWith(AppConfig.Instance.CacheDir));
        
        using var db = GetDbConnection();
        AppConfig.Instance.ModelUsers = db.Select(db.From<ApplicationUser>().Where(x => x.Model != null || x.UserName == "human"));
    }
    
    private string? ResolveGitBlobBaseUrl(IVirtualDirectory contentDir)
    {
        var srcDir = new DirectoryInfo(contentDir.RealPath);
        var gitConfig = new FileInfo(Path.Combine(srcDir.Parent!.FullName, ".git", "config"));
        if (gitConfig.Exists)
        {
            var txt = gitConfig.ReadAllText();
            var pos = txt.IndexOf("url = ", StringComparison.Ordinal);
            if (pos >= 0)
            {
                var url = txt[(pos + "url = ".Length)..].LeftPart(".git").LeftPart('\n').Trim();
                var gitBaseUrl = url.CombineWith($"blob/main/{srcDir.Name}");
                return gitBaseUrl;
            }
        }
        return null;
    }
}

public static class HtmlHelpers
{
    public static string ToAbsoluteContentUrl(string? relativePath) => HostContext.DebugMode 
        ? AppConfig.Instance.LocalBaseUrl.CombineWith(relativePath)
        : AppConfig.Instance.PublicBaseUrl.CombineWith(relativePath);
    public static string ToAbsoluteApiUrl(string? relativePath) => HostContext.DebugMode 
        ? AppConfig.Instance.LocalBaseUrl.CombineWith(relativePath)
        : AppConfig.Instance.PublicBaseUrl.CombineWith(relativePath);

    public static string ContentUrl(this IHtmlHelper html, string? relativePath) => ToAbsoluteContentUrl(relativePath); 
    public static string ApiUrl(this IHtmlHelper html, string? relativePath) => ToAbsoluteApiUrl(relativePath);
}
