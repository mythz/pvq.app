﻿using System.Collections.Concurrent;
using Amazon.S3;
using NUnit.Framework;
using ServiceStack;
using ServiceStack.IO;
using ServiceStack.Text;

namespace MyApp.Tests;

[TestFixture, Explicit]
public class R2Tests
{
    AmazonS3Client CreateS3Client() => new(
        Environment.GetEnvironmentVariable("R2_ACCESS_KEY_ID"), 
        Environment.GetEnvironmentVariable("R2_SECRET_ACCESS_KEY"), 
        new AmazonS3Config {
            ServiceURL = $"https://{Environment.GetEnvironmentVariable("R2_ACCOUNT_ID")}.r2.cloudflarestorage.com"
        });

    R2VirtualFiles CreateR2VirtualFiles() => new(CreateS3Client(), "stackoverflow-shootout");    

    [Test]
    public async Task Can_list_and_download_all_question_files()
    {
        var r2 = CreateR2VirtualFiles();
        var files = (await r2.EnumerateFilesAsync("000/105").ToListAsync())
            .Where(x => x.Name.Glob("372.*"))
            .OrderByDescending(x => x.LastModified)
            .ToArray();
        files.Each(x => $"{x.Name}: {x.LastModified}".Print());
        
        async Task<string> GetFileContents(IVirtualFile file) => await file.ReadAllTextAsync();

        var fileContents = new ConcurrentDictionary<string, string>();
        var tasks = new List<Task>();
        tasks.AddRange(files.Select(async x =>
        {
            fileContents[x.Name] = await GetFileContents(x);
        }));
        await Task.WhenAll(tasks);
        
        fileContents.Each(x =>
        {
            Console.WriteLine(x.Key + ":");
            Console.WriteLine(x.Value);
            Console.WriteLine();
        });
    }
}
