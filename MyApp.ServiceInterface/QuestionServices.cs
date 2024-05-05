﻿using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MyApp.Data;
using MyApp.ServiceInterface.App;
using ServiceStack;
using MyApp.ServiceModel;
using ServiceStack.IO;
using ServiceStack.OrmLite;

namespace MyApp.ServiceInterface;

public class QuestionServices(ILogger<QuestionServices> log,
    AppConfig appConfig, 
    QuestionsProvider questions, 
    RendererCache rendererCache, 
    WorkerAnswerNotifier answerNotifier,
    ICommandExecutor executor) : Service
{
    private List<string> ValidateQuestionTags(List<string>? tags)
    {
        var validTags = (tags ?? []).Select(x => x.Trim().ToLower()).Where(x => appConfig.AllTags.Contains(x)).ToList();

        if (validTags.Count == 0)
            throw new ArgumentException("At least 1 tag is required", nameof(tags));

        if (validTags.Count > 5)
            throw new ArgumentException("Maximum of 5 tags allowed", nameof(tags));
        return validTags;
    }

    public async Task<object> Get(GetAllAnswerModels request)
    {
        var question = await questions.GetQuestionAsync(request.Id);
        var modelNames = question.Question?.Answers
            .Where(x => x.CreatedBy != null && !appConfig.IsHuman(x.CreatedBy))
            .Select(x => x.CreatedBy!)
            .ToList() ?? [];

        return new GetAllAnswerModelsResponse
        {
            Results = modelNames
        };
    }
    
    static Regex AlphaNumericRegex = new("[^a-zA-Z0-9]", RegexOptions.Compiled);
    static Regex SingleWhiteSpaceRegex = new( @"\s+", RegexOptions.Multiline | RegexOptions.Compiled);

    public async Task<object> Any(FindSimilarQuestions request)
    {
        var searchPhrase = AlphaNumericRegex.Replace(request.Text, " ");
        searchPhrase = SingleWhiteSpaceRegex.Replace(searchPhrase, " ").Trim();
        if (searchPhrase.Length < 15)
            throw new ArgumentException("Search text must be at least 20 characters", nameof(request.Text));

        using var dbSearch = HostContext.AppHost.GetDbConnection(Databases.Search);
        var q = dbSearch.From<PostFts>()
            .Where("Body match {0} AND instr(RefId,'-') == 0", searchPhrase)
            .OrderBy("rank")
            .Limit(10);
        
        var results = await dbSearch.SelectAsync(q);
        var posts = await Db.PopulatePostsAsync(results);

        return new FindSimilarQuestionsResponse
        {
            Results = posts
        };
    }

    public async Task<object> Any(AskQuestion request)
    {
        var tags = ValidateQuestionTags(request.Tags);

        var userName = GetUserName();
        var now = DateTime.UtcNow;
        var title = request.Title.Trim();
        var body = request.Body.Trim();
        var slug = request.Title.GenerateSlug(200);
        var summary = request.Body.GenerateSummary();

        var existingPost = await Db.SingleAsync(Db.From<Post>().Where(x => x.Title == title));
        if (existingPost != null)
            throw new ArgumentException($"Question with title '{title}' already used in question {existingPost.Id}", nameof(Post.Title));

        var refUrn = request.RefUrn;
        var postId = refUrn != null && refUrn.StartsWith("stackoverflow.com:")
               && int.TryParse(refUrn.LastRightPart(':'), out var stackoverflowPostId)
               && !await Db.ExistsAsync<Post>(x => x.Id == stackoverflowPostId)
            ? stackoverflowPostId
            :  (int)appConfig.GetNextPostId();
            
        Post createPost() => new()
        {
            Id = postId,
            PostTypeId = 1,
            Title = title,
            Tags = tags,
            Slug = slug,
            Summary = summary,
            CreationDate = now,
            CreatedBy = userName,
            LastActivityDate = now,
            Body = body,
            RefId = $"{postId}",
            RefUrn = refUrn,
        };

        var post = createPost();
        var dbPost = createPost();
        MessageProducer.Publish(new DbWrites
        {
            CreatePost = dbPost,
        });
        
        MessageProducer.Publish(new AiServerTasks
        {
            CreateAnswerTasks = new() {
                Post = post,
                ModelUsers = appConfig.GetAnswerModelUsersFor(userName),
            }
        });

        MessageProducer.Publish(new DiskTasks
        {
            SaveQuestion = post,
        });
       
        return new AskQuestionResponse
        {
            Id = post.Id,
            Slug = post.Slug,
            RedirectTo = $"/answers/{post.Id}/{post.Slug}"
        };
    }

    public async Task<object> Any(DeleteQuestion request)
    {
        await questions.DeleteQuestionFilesAsync(request.Id);
        rendererCache.DeleteCachedQuestionPostHtml(request.Id);
        MessageProducer.Publish(new DbWrites
        {
            DeletePost = new() { Ids = [request.Id] },
        });
        MessageProducer.Publish(new SearchTasks
        {
            DeletePost = request.Id,
        });

        if (request.ReturnUrl != null && request.ReturnUrl.StartsWith('/') && request.ReturnUrl.IndexOf(':') < 0)
            return HttpResult.Redirect(request.ReturnUrl, HttpStatusCode.TemporaryRedirect);
        
        return new EmptyResponse();
    }

    public async Task<object> Any(AnswerQuestion request)
    {
        var userName = GetUserName();
        var now = DateTime.UtcNow;
        var postId = request.PostId;
        var answerId = $"{postId}-{userName}";
        var answer = new Post
        {
            ParentId = postId,
            Summary = request.Body.GenerateSummary(),
            CreationDate = now,
            CreatedBy = userName,
            LastActivityDate = now,
            Body = request.Body,
            RefUrn = request.RefUrn,
            RefId = answerId
        };
        
        MessageProducer.Publish(new DbWrites {
            CreateAnswer = answer,
            AnswerAddedToPost = new() { Id = postId },
        });
        
        rendererCache.DeleteCachedQuestionPostHtml(postId);

        await questions.SaveAnswerAsync(answer);

        MessageProducer.Publish(new DbWrites
        {
            SaveStartingUpVotes = new()
            {
                Id = answerId,
                PostId = postId,
                StartingUpVotes = 0,
                CreatedBy = userName,
                LastUpdated = DateTime.UtcNow,
            }
        });
        
        answerNotifier.NotifyNewAnswer(postId, answer.CreatedBy);

        var userId = Request.GetClaimsPrincipal().GetUserId();
        MessageProducer.Publish(new AiServerTasks
        {
            CreateRankAnswerTask = new CreateRankAnswerTask {
                AnswerId = answerId,
                UserId = userId!,
            } 
        });
        
        MessageProducer.Publish(new SearchTasks {
            AddAnswerToIndex = answerId
        });

        return new AnswerQuestionResponse();
    }

    /* /100/000
     *   001.json <Post>
     * Edit 1:
     *   001.json <Post> Updated
     *   edit.q.100000001-user_20240301-1200.json // original question
     */
    public async Task<object> Any(UpdateQuestion request)
    {
        var question = await Db.SingleByIdAsync<Post>(request.Id);
        if (question == null)
            throw HttpError.NotFound("Question does not exist");
        
        var userName = GetUserName();
        var isModerator = Request.GetClaimsPrincipal().HasRole(Roles.Moderator);
        if (!isModerator && question.CreatedBy != userName)
        {
            var userInfo = await Db.SingleAsync<UserInfo>(x => x.UserName == userName);
            if (userInfo.Reputation < 10)
                throw HttpError.Forbidden("You need at least 10 reputation to Edit other User's Questions.");
        }

        question.Title = request.Title;
        question.Tags = ValidateQuestionTags(request.Tags);
        question.Slug = request.Title.GenerateSlug(200);
        question.Summary = request.Body.GenerateSummary();
        question.Body = request.Body;
        question.ModifiedBy = userName;
        question.LastActivityDate = DateTime.UtcNow;
        question.LastEditDate = question.LastActivityDate;

        MessageProducer.Publish(new DbWrites
        {
            UpdatePost = question,
        });
        await questions.SaveQuestionEditAsync(question);

        return new UpdateQuestionResponse
        {
            Result = question
        };
    }

    /* /100/000
     *   001.h.model.json <OpenAI>
     * Edit 1:
     *   001.h.model.json <Post>
     *   edit.h.100000001-model_20240301-1200.json // original model answer, Modified Date <OpenAI>
     */
    public async Task<object> Any(UpdateAnswer request)
    {
        var answerFile = await questions.GetAnswerFileAsync(request.Id);
        if (answerFile == null)
            throw HttpError.NotFound("Answer does not exist");

        var userName = GetUserName();
        var isModerator = Request.GetClaimsPrincipal().HasRole(Roles.Moderator);
        if (!isModerator && !answerFile.Name.Contains(userName))
        {
            var userInfo = await Db.SingleAsync<UserInfo>(x => x.UserName == userName);
            if (userInfo.Reputation < 100)
                throw HttpError.Forbidden("You need at least 100 reputation to Edit other User's Answers.");
        }

        await questions.SaveAnswerEditAsync(answerFile, userName, request.Body, request.EditReason);

        return new UpdateAnswerResponse();
    }

    public async Task<object> Any(GetQuestion request)
    {
        var question = await Db.SingleByIdAsync<Post>(request.Id);
        if (question == null)
            throw HttpError.NotFound($"Question {request.Id} not found");

        return new GetQuestionResponse
        {
            Result = question
        };
    }

    private async Task<IVirtualFile?> AssetQuestionFile(int id)
    {
        var questionFile = await questions.GetQuestionFileAsync(id);
        if (questionFile == null)
            throw HttpError.NotFound($"Question {id} not found");
        return questionFile;
    }

    public async Task<object> Any(GetQuestionFile request)
    {
        var questionFile = await AssetQuestionFile(request.Id);
        var json = await questionFile.ReadAllTextAsync();
        return new HttpResult(json, MimeTypes.Json);
    }

    public async Task<object> Any(GetQuestionBody request)
    {
        var questionFile = await AssetQuestionFile(request.Id);
        var json = await questionFile.ReadAllTextAsync();
        var post = json.FromJson<Post>();
        return post.Body;
    }
    
    public async Task<object> Get(GetAnswerFile request)
    {
        var answerFile = await questions.GetAnswerFileAsync(request.Id);
        if (answerFile == null)
            throw HttpError.NotFound("Answer does not exist");

        var json = await answerFile.ReadAllTextAsync();
        return new HttpResult(json, MimeTypes.Json);
    }

    public async Task<object> Get(GetAnswer request)
    {
        var answerFile = await questions.GetAnswerFileAsync(request.Id);
        if (answerFile == null)
            throw HttpError.NotFound("Answer does not exist");

        var post = await questions.GetAnswerAsPostAsync(answerFile);
        return new GetAnswerResponse
        {
            Result = post
        };
    }

    public async Task<object> Any(GetAnswerBody request)
    {
        var body = await questions.GetAnswerBodyAsync(request.Id);
        if (body == null)
            throw HttpError.NotFound("Answer does not exist");

        return new HttpResult(body, MimeTypes.PlainText);
    }

    public async Task<object> Any(CreateComment request)
    {
        var question = await AssertValidQuestion(request.Id);
        if (question.LockedDate != null)
            throw HttpError.Conflict($"{question.GetPostType()} is locked");

        var createdBy = GetUserName();

        // If the comment is for a model answer, have the model respond to the comment
        var answerCreator = request.Id.Contains('-')
            ? request.Id.RightPart('-')
            : null;
        var modelCreator = answerCreator != null 
            ? appConfig.GetModelUser(answerCreator) 
            : null;

        if (modelCreator?.UserName != null)
        {
            var canUseModel = appConfig.CanUseModel(createdBy, modelCreator.UserName);
            if (!canUseModel)
            {
                var userCount = appConfig.GetQuestionCount(createdBy);
                log.LogWarning("User {UserName} ({UserCount}) attempted to use model {ModelUserName} ({ModelCount})", 
                    createdBy, userCount, modelCreator.UserName, appConfig.GetModelLevel(modelCreator.UserName));
                throw HttpError.Forbidden("You have not met the requirements to access this model");
            }
        }

        var postId = question.Id;
        var meta = await questions.GetMetaAsync(postId);
        
        meta.Comments ??= new();
        var comments = meta.Comments.GetOrAdd(request.Id, key => new());
        var body = request.Body.GenerateComment();
        
        var newComment = new Comment
        {
            Body = body,
            Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CreatedBy = createdBy,
        };
        comments.Add(newComment);
        
        MessageProducer.Publish(new DbWrites
        {
            NewComment = new()
            {
                RefId = request.Id,
                Comment = newComment,
            },
        });

        await questions.SaveMetaAsync(postId, meta);

        if (modelCreator?.UserName != null)
        {
            var answer = await questions.GetAnswerAsPostAsync(request.Id);
            if (answer != null)
            {
                var mention = $"@{createdBy}";
                var userConversation = comments
                    .Where(x => x.CreatedBy == createdBy || x.Body.Contains(mention))
                    .ToList();
                
                var createdById = GetUserId();
                var model = modelCreator.Model ?? throw new ArgumentNullException(nameof(modelCreator.Model));
                MessageProducer.Publish(new AiServerTasks
                {
                    CreateAnswerCommentTask = new()
                    {
                        Model = model,
                        Question = question,
                        Answer = answer,
                        UserName = newComment.CreatedBy,
                        UserId = createdById,
                        Comments = userConversation,
                    }
                });
            }
            else
            {
                log.LogError("Answer {Id} not found", request.Id);
            }
        }
        
        return new CommentsResponse
        {
            Comments = comments
        };
    }

    private async Task<Post> AssertValidQuestion(string refId)
    {
        var postId = refId.LeftPart('-').ToInt();
        var postType = refId.IndexOf('-') >= 0 ? "Answer" : "Question";

        var question = await questions.GetQuestionFileAsPostAsync(postId);
        if (question == null)
            throw HttpError.NotFound($"{postType} {postId} not found");

        return question;
    }

    public async Task<object> Any(DeleteComment request)
    {
        var question = await AssertValidQuestion(request.Id);

        var userName = GetUserName();
        var isModerator = Request.GetClaimsPrincipal().HasRole(Roles.Moderator);
        if (userName != request.CreatedBy && !isModerator)
            throw HttpError.Forbidden("Only Moderators can delete other user's comments");
        
        MessageProducer.Publish(new DbWrites
        {
            DeleteComment = request, 
        });
        
        var postId = question.Id;
        var meta = await questions.GetMetaAsync(postId);
        
        meta.Comments ??= new();
        if (meta.Comments.TryGetValue(request.Id, out var comments))
        {
            comments.RemoveAll(x => x.Created == request.Created && x.CreatedBy == request.CreatedBy);
            await questions.SaveMetaAsync(postId, meta);
            return new CommentsResponse { Comments = comments };
        }

        return new CommentsResponse { Comments = [] };
    }

    public async Task<object> Any(GetMeta request)
    {
        var postId = request.Id.LeftPart('-').ToInt();
        var metaFile = await questions.GetMetaFileAsync(postId);
        var metaJson = metaFile != null
            ? await metaFile.ReadAllTextAsync()
            : "{}";
        var meta = metaJson.FromJson<Meta>();
        return meta;
    }

    public object Any(GetUserReputations request)
    {
        var to = new GetUserReputationsResponse();
        foreach (var userName in request.UserNames.Safe())
        {
            to.Results[userName] = appConfig.GetReputation(userName);
        }
        return to;
    }

    private string GetUserName()
    {
        var userName = Request.GetClaimsPrincipal().GetUserName()
            ?? throw new ArgumentNullException(nameof(ApplicationUser.UserName));
        return userName;
    }

    private string GetUserId()
    {
        var userId = Request.GetClaimsPrincipal().GetUserId()
            ?? throw new ArgumentNullException(nameof(ApplicationUser.Id));
        return userId;
    }

    public async Task<object> Any(ImportQuestion request)
    {
        var command = executor.Command<ImportQuestionCommand>();
        await executor.ExecuteAsync(command, request);
        return new ImportQuestionResponse
        {
            Result = command.Result
                ?? throw new Exception("Import failed")
        };
    }

    public async Task<object> Any(GetLastUpdated request)
    {
        var lastUpdated = request.Id != null
            ? await Db.ScalarAsync<DateTime?>(Db.From<StatTotals>().Where(x => x.Id == request.Id)
                .Select(x => x.LastUpdated))
            : request.PostId != null
                ? await Db.ScalarAsync<DateTime?>(Db.From<StatTotals>().Where(x => x.PostId == request.PostId)
                    .Select(x => Sql.Max(x.LastUpdated)))
                : null;

        return new GetLastUpdatedResponse
        {
            Result = lastUpdated
        };
    }
}

[ValidateHasRole(Roles.Moderator)]
public class GetAnswerFile : IGet, IReturn<string>
{
    /// <summary>
    /// Format is {PostId}-{UserName}
    /// </summary>
    public string Id { get; set; }
}
public class GetAllAnswerModelsResponse
{
    public List<string> Results { get; set; }
}

public class GetAnswer : IGet, IReturn<GetAnswerResponse>
{
    /// <summary>
    /// Format is {PostId}-{UserName}
    /// </summary>
    public string Id { get; set; }
}
public class GetAnswerResponse
{
    public Post Result { get; set; }
}

[ValidateIsAuthenticated]
public class GetAllAnswerModels : IReturn<GetAllAnswerModelsResponse>, IGet
{
    public int Id { get; set; }
}
