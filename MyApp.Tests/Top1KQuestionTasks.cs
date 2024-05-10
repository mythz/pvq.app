﻿using MyApp.ServiceModel;
using NUnit.Framework;
using ServiceStack;
using ServiceStack.Text;

namespace MyApp.Tests;

[Explicit]
public class Top1KQuestionTasks
{
    private List<string> top10Models =
    [
        "claude3-opus",
        "claude3-sonnet",
        "claude3-haiku",
        "mixtral",
        "gemini-pro",
        "mistral",
        "gemma",
        "deepseek-coder",
        "gemma-2b",
    ];

    [Test]
    public async Task Find_Missing_Top1K_Questions_For_Model()
    {
        var model = "mixtral";

        var client = TestUtils.CreateProdClient();
        
        var api = await client.ApiAsync(new MissingTop1K
        {
            Model = model,
        });
        
        api.Response.PrintDump();
    }

    [Test]
    public async Task Create_missing_Top1K_Answers_for_Models()
    {
        // var model = "mixtral";

        var client = TestUtils.CreateProdClient();
        await client.ApiAsync(new Authenticate
        {
            provider = "credentials",
            UserName = "mythz",
            Password = Environment.GetEnvironmentVariable("AUTH_SECRET")
        });

        foreach (var model in top10Models)
        {
            await CreateMissing1KModelsForModelAsync(client, model);
        }
    }

    [Test]
    public async Task Create_missing_Top1K_Answers_for_Adhoc_Model()
    {
        var model = "command-r-plus";

        var client = await TestUtils.CreateAuthenticatedProdClientAsync();
        await CreateMissing1KModelsForModelAsync(client, model);
    }

    private static async Task CreateMissing1KModelsForModelAsync(JsonApiClient client, string model)
    {
        var api = await client.ApiAsync(new MissingTop1K
        {
            Model = model,
        });
            
        api.Error.PrintDump();
        api.ThrowIfError();
        api.Response.PrintDump();

        if (api.Response!.Results.Count == 0)
        {
            $"No more missing questions for {model}".Print();
            return;
        }

        var apiCreate = await client.ApiAsync(new CreateAnswersForModel
        {
            Model = model,
            PostIds = api.Response!.Results,
        });
            
        apiCreate.Error.PrintDump();
        apiCreate.ThrowIfError();
        apiCreate.Response!.Errors.PrintDump();
        apiCreate.Response!.Results.PrintDump();
    }

    [Test]
    public async Task Find_answers_that_have_not_been_individually_graded()
    {
        var client = await TestUtils.CreateAuthenticatedProdClientAsync();
        // var client = await TestUtils.CreateAuthenticatedDevClientAsync();
        
        var api = await client.ApiAsync(new MissingGradedAnswersTop1K());
            
        api.Error.PrintDump();
        api.ThrowIfError();
        api.Response.PrintDump();

        if (api.Response!.Results.Count == 0)
        {
            $"No more ungraded answers".Print();
            return;
        }

        var apiCreate = await client.ApiAsync(new CreateRankingTasks
        {
            AnswerIds = api.Response!.Results,
        });
            
        apiCreate.Error.PrintDump();
        apiCreate.ThrowIfError();
        apiCreate.Response!.Errors.PrintDump();
        apiCreate.Response!.Results.PrintDump();
    }
}