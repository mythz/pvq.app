﻿using Microsoft.Extensions.Logging;
using MyApp.ServiceModel;
using ServiceStack;
using ServiceStack.IO;
using ServiceStack.Messaging;
using ServiceStack.Text;

namespace MyApp.Data;

public class QuestionsProvider(ILogger<QuestionsProvider> log, IMessageProducer mqClient, IVirtualFiles fs, R2VirtualFiles r2)
{
    public const int MostVotedScore = 10;
    public const int AcceptedScore = 9;
    public static Dictionary<string,int> ModelScores = new()
    {
        ["phi"] = 1, //2.7B
        ["gemma:2b"] = 2,
        ["qwen:4b"] = 3, //4B
        ["codellama"] = 4, //7B
        ["gemma"] = 5, //7B
        ["deepseek-coder:6.7b"] = 5, //6.7B
        ["mistral"] = 7, //7B
        ["mixtral"] = 8, //47B
        ["accepted"] = 9,
        ["most-voted"] = 10,
    };

    public List<string> GetAnswerModelsFor(string? userName)
    {
#if DEBUG
        return ["phi", "gemma:2b", "gemma", "mixtral"];
#else
        return ["phi", "gemma:2b", "qwen:4b", "codellama", "gemma", "deepseek-coder:6.7b", "mistral", "mixtral"];
#endif
    }

    public System.Text.Json.JsonSerializerOptions SystemJsonOptions = new(TextConfig.SystemJsonOptions)
    {
        WriteIndented = true
    };

    public string ToJson<T>(T obj) => System.Text.Json.JsonSerializer.Serialize(obj, SystemJsonOptions); 
    
    public QuestionFiles GetLocalQuestionFiles(int id)
    {
        var (dir1, dir2, fileId) = id.ToFileParts();
        
        var files = fs.GetDirectory($"{dir1}/{dir2}").GetAllMatchingFiles($"{fileId}.*")
            .OrderByDescending(x => x.LastModified)
            .ToList();
        
        return new QuestionFiles(id: id, dir1: dir1, dir2: dir2, fileId: fileId, files: files);
    }

    public async Task<QuestionFiles> GetRemoteQuestionFilesAsync(int id)
    {
        var (dir1, dir2, fileId) = id.ToFileParts();
        
        var files = (await r2.EnumerateFilesAsync($"{dir1}/{dir2}").ToListAsync())
            .Where(x => x.Name.Glob($"{fileId}.*"))
            .OrderByDescending(x => x.LastModified)
            .Cast<IVirtualFile>()
            .ToList();
        
        return new QuestionFiles(id: id, dir1: dir1, dir2: dir2, fileId: fileId, files: files, remote:true);
    }

    public static string GetQuestionPath(int id)
    {
        var (dir1, dir2, fileId) = id.ToFileParts();
        var path = $"{dir1}/{dir2}/{fileId}.json";
        return path;
    }

    public static string GetModelAnswerPath(int id, string model)
    {
        var (dir1, dir2, fileId) = id.ToFileParts();
        var path = $"{dir1}/{dir2}/{fileId}.a.{model.Replace(':','-')}.json";
        return path;
    }

    public static string GetHumanAnswerPath(int id, string userName)
    {
        var (dir1, dir2, fileId) = id.ToFileParts();
        var path = $"{dir1}/{dir2}/{fileId}.h.{userName}.json";
        return path;
    }

    public static string GetMetaPath(int id)
    {
        var (dir1, dir2, fileId) = id.ToFileParts();
        var path = $"{dir1}/{dir2}/{fileId}.meta.json";
        return path;
    }
    
    public async Task SaveFileAsync(string virtualPath, string contents)
    {
        await Task.WhenAll(
            r2.WriteFileAsync(virtualPath, contents),
            fs.WriteFileAsync(virtualPath, contents));
    }

    public async Task WriteMetaAsync(Meta meta)
    {
        await SaveFileAsync(GetMetaPath(meta.Id), ToJson(meta));
    }

    public async Task<QuestionFiles> GetQuestionFilesAsync(int id)
    {
        var localFiles = GetLocalQuestionFiles(id);
        if (localFiles.Files.Count > 0)
            return localFiles;

        log.LogInformation("No local cached files for question {Id}, fetching from R2...", id);
        var r = await GetRemoteQuestionFilesAsync(id);
        if (r.Files.Count > 0)
        {
            var lastModified = r.Files.Max(x => x.LastModified);
            log.LogInformation("Fetched {Count} files from R2 for question {Id}, last modified: '{LastModified}'", r.Files.Count, id, lastModified);
        }
        return r;
    }

    public async Task<QuestionFiles> GetQuestionAsync(int id)
    {
        var questionFiles = await GetQuestionFilesAsync(id);
        await questionFiles.GetQuestionAsync();
        if (questionFiles.LoadedRemotely)
        {
            log.LogInformation("Caching question {Id}'s {Count} remote files locally...", id, questionFiles.FileContents.Count);
            foreach (var entry in questionFiles.FileContents)
            {
                await fs.WriteFileAsync(entry.Key, entry.Value);
            }
        }
        return questionFiles;
    }

    public async Task SaveQuestionAsync(Post post)
    {
        await SaveFileAsync(GetQuestionPath(post.Id), ToJson(post));
    }

    public async Task SaveModelAnswerAsync(int postId, string model, string json)
    {
        await SaveFileAsync(GetModelAnswerPath(postId, model), json);
    }

    public async Task SaveHumanAnswerAsync(Post post)
    {
        await SaveFileAsync(GetHumanAnswerPath(
            post.ParentId ?? throw new ArgumentNullException(nameof(Post.ParentId)), 
            post.CreatedBy ?? throw new ArgumentNullException(nameof(Post.CreatedBy))), 
            ToJson(post));
    }

    public async Task SaveHumanAnswerAsync(int postId, string userName, string json)
    {
        await SaveFileAsync(GetHumanAnswerPath(postId, userName), json);
    }

    public async Task SaveLocalFileAsync(string virtualPath, string contents)
    {
        await fs.WriteFileAsync(virtualPath, contents);
    }
    
    public async Task SaveRemoteFileAsync(string virtualPath, string contents)
    {
        await r2.WriteFileAsync(virtualPath, contents);
    }

    public async Task DeleteQuestionFilesAsync(int id)
    {
        var localQuestionFiles = GetLocalQuestionFiles(id);
        fs.DeleteFiles(localQuestionFiles.Files.Select(x => x.VirtualPath));
        var remoteQuestionFiles = await GetRemoteQuestionFilesAsync(id);
        r2.DeleteFiles(remoteQuestionFiles.Files.Select(x => x.VirtualPath));
    }
}
