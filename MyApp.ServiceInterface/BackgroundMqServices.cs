﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyApp.Data;
using MyApp.ServiceInterface.App;
using MyApp.ServiceModel;
using ServiceStack;
using ServiceStack.IO;
using ServiceStack.OrmLite;

namespace MyApp.ServiceInterface;

public class BackgroundMqServices(
    IServiceProvider services,
    ILogger<BackgroundMqServices> log,
    AppConfig appConfig, 
    R2VirtualFiles r2, 
    ModelWorkerQueue modelWorkers) 
    : MqServicesBase(log, appConfig)
{
    public async Task Any(DiskTasks request)
    {
        var saveFile = request.SaveFile;
        if (saveFile != null)
        {
            if (saveFile.Stream != null)
            {
                await r2.WriteFileAsync(saveFile.FilePath, saveFile.Stream);
            }
            else if (saveFile.Text != null)
            {
                await r2.WriteFileAsync(saveFile.FilePath, saveFile.Text);
            }
            else if (saveFile.Bytes != null)
            {
                await r2.WriteFileAsync(saveFile.FilePath, saveFile.Bytes);
            }
        }

        if (request.CdnDeleteFiles != null)
        {
            r2.DeleteFiles(request.CdnDeleteFiles);
        }
    }

    ILogger<T> GetLogger<T>() => services.GetRequiredService<ILogger<T>>();

    public async Task Any(DbWrites request)
    {
        if (request.CreatePostVote != null)
            await ExecuteAsync(new CreatePostVotesCommand(AppConfig, Db, MessageProducer), request.CreatePostVote);

        if (request.CreatePost != null)
            await ExecuteAsync(new CreatePostCommand(GetLogger<CreatePostCommand>(), AppConfig, Db), request.CreatePost);

        if (request.UpdatePost != null)
            await ExecuteAsync(new UpdatePostCommand(Db), request.UpdatePost);

        if (request.DeletePost != null)
            await ExecuteAsync(new DeletePostCommand(AppConfig, Db), request.DeletePost);
        
        if (request.CreatePostJobs is { PostJobs.Count: > 0 })
            await ExecuteAsync(new CreatePostJobsCommand(Db, modelWorkers), request.CreatePostJobs);

        if (request.StartJob != null)
            await ExecuteAsync(new StartJobCommand(Db), request.StartJob);

        if (request.CompletePostJobs is { Ids.Count: > 0 })
            await ExecuteAsync(new CompletePostJobsCommand(Db, modelWorkers, MessageProducer), request.CompletePostJobs);

        if (request.FailJob != null)
            await ExecuteAsync(new FailJobCommand(Db, modelWorkers), request.FailJob);

        if (request.CreateAnswer != null)
            await ExecuteAsync(new CreateAnswerCommand(AppConfig, Db), request.CreateAnswer);
        
        if (request.CreateNotification != null)
            await ExecuteAsync(new CreateNotificationCommand(AppConfig, Db), request.CreateNotification);

        if (request.AnswerAddedToPost != null)
            await ExecuteAsync(new AnswerAddedToPostCommand(Db), request.AnswerAddedToPost);

        if (request.NewComment != null)
            await ExecuteAsync(new NewCommentCommand(AppConfig, Db), request.NewComment);

        if (request.DeleteComment != null)
            await ExecuteAsync(new DeleteCommentCommand(AppConfig, Db), request.DeleteComment);

        if (request.UpdateReputations != null)
            await ExecuteAsync(new UpdateReputationsCommand(AppConfig, Db), request.UpdateReputations);

        if (request.MarkAsRead != null)
            await ExecuteAsync(new MarkAsReadCommand(AppConfig, Db), request.MarkAsRead);
    }

    public async Task Any(AnalyticsTasks request)
    {
        if (request.RecordPostView == null && request.RecordSearchView == null && request.DeletePost == null)
            return;

        using var analyticsDb = HostContext.AppHost.GetDbConnection(Databases.Analytics);
        
        if (request.RecordPostView != null)// && !Stats.IsAdminOrModerator(request.RecordPostView.UserName))
        {
            await analyticsDb.InsertAsync(request.RecordPostView);
        }

        if (request.RecordSearchView != null)// && !Stats.IsAdminOrModerator(request.RecordSearchView.UserName))
        {
            await analyticsDb.InsertAsync(request.RecordSearchView);
        }

        if (request.DeletePost != null)
        {
            await analyticsDb.DeleteAsync<PostView>(x => x.PostId == request.DeletePost);
        }
    }

    public object Any(ViewCommands request)
    {
        var to = new ViewCommandsResponse
        {
            LatestCommands = new(AppConfig.CommandResults),
            LatestFailed = new(AppConfig.CommandFailures),
            CommandTotals = new(AppConfig.CommandTotals.Values)
        };
        if (request.Clear == true)
        {
            AppConfig.CommandResults.Clear();
            AppConfig.CommandFailures.Clear();
            AppConfig.CommandTotals.Clear();
        }
        return to;
    }
}
