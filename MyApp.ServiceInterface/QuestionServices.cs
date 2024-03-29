﻿using MyApp.Data;
using ServiceStack;
using MyApp.ServiceModel;
using ServiceStack.OrmLite;

namespace MyApp.ServiceInterface;

public class QuestionServices(AppConfig appConfig, 
    QuestionsProvider questions, 
    RendererCache rendererCache, 
    WorkerAnswerNotifier answerNotifier) : Service
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

    public async Task<object> Any(AskQuestion request)
    {
        var tags = ValidateQuestionTags(request.Tags);

        var userName = GetUserName();
        var now = DateTime.UtcNow;
        var postId = (int)appConfig.GetNextPostId(); 
        var slug = request.Title.GenerateSlug(200);
        var summary = request.Body.StripHtml().SubstringWithEllipsis(0, 200);
        
        Post createPost() => new()
        {
            Id = postId,
            PostTypeId = 1,
            Title = request.Title,
            Tags = tags,
            Slug = slug,
            Summary = summary,
            CreationDate = now,
            CreatedBy = userName,
            LastActivityDate = now,
            Body = request.Body,
            RefId = request.RefId,
        };

        var post = createPost();
        var dbPost = createPost();
        dbPost.Body = null;
        MessageProducer.Publish(new DbWrites
        {
            CreatePost = dbPost,
            CreatePostJobs = questions.GetAnswerModelsFor(userName)
                .Select(model => new PostJob
                {
                    PostId = post.Id,
                    Model = model,
                    Title = request.Title,
                    CreatedBy = userName,
                    CreatedDate = now,
                }).ToList(),
        });

        await questions.SaveQuestionAsync(post);
       
        return new AskQuestionResponse
        {
            Id = post.Id,
            Slug = post.Slug,
            RedirectTo = $"/answers/{post.Id}/{post.Slug}"
        };
    }

    public async Task<EmptyResponse> Any(DeleteQuestion request)
    {
        await questions.DeleteQuestionFilesAsync(request.Id);
        rendererCache.DeleteCachedQuestionPostHtml(request.Id);
        MessageProducer.Publish(new DbWrites
        {
            DeletePost = request.Id,
        });
        return new EmptyResponse();
    }

    public async Task<object> Any(AnswerQuestion request)
    {
        var userName = GetUserName();
        var now = DateTime.UtcNow;
        var post = new Post
        {
            ParentId = request.PostId,
            Summary = request.Body.StripHtml().SubstringWithEllipsis(0,200),
            CreationDate = now,
            CreatedBy = userName,
            LastActivityDate = now,
            Body = request.Body,
            RefId = request.RefId,
        };
        
        await questions.SaveHumanAnswerAsync(post);
        rendererCache.DeleteCachedQuestionPostHtml(post.Id);
        
        // Rewind last Id if it was latest question
        var maxPostId = Db.Scalar<int>("SELECT MAX(Id) FROM Post");
        AppConfig.Instance.SetInitialPostId(Math.Max(100_000_000, maxPostId));
        
        answerNotifier.NotifyNewAnswer(request.PostId, post.CreatedBy);

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
            throw HttpError.Forbidden("Only moderators can update other user's questions.");

        question.Title = request.Title;
        question.Tags = ValidateQuestionTags(request.Tags);
        question.Slug = request.Title.GenerateSlug(200);
        question.Summary = request.Body.StripHtml().SubstringWithEllipsis(0, 200);
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
     *   001.a.model.json <OpenAI>
     * Edit 1:
     *   001.h.model.json <Post>
     *   edit.a.100000001-model_20240301-1200.json // original model answer, Modified Date <OpenAI>
     */
    public async Task<object> Any(UpdateAnswer request)
    {
        var answerFile = await questions.GetAnswerFileAsync(request.Id);
        if (answerFile == null)
            throw HttpError.NotFound("Answer does not exist");

        var userName = GetUserName();
        var isModerator = Request.GetClaimsPrincipal().HasRole(Roles.Moderator);
        if (!isModerator && !answerFile.Name.Contains(userName))
            throw HttpError.Forbidden("Only moderators can update other user's answers.");

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

    public async Task<object> Any(GetQuestionFile request)
    {
        var questionFile = await questions.GetQuestionFileAsync(request.Id);
        if (questionFile == null)
            throw HttpError.NotFound($"Question {request.Id} not found");
        
        //TODO: Remove Hack when all files are converted to camelCase
        var json = await questionFile.ReadAllTextAsync();
        if (json.Trim().StartsWith("{\"Id\":"))
        {
            var post = json.FromJson<Post>();
            var camelCaseJson = questions.ToJson(post);
            return new HttpResult(camelCaseJson, MimeTypes.Json);
        }
        
        return new HttpResult(json, MimeTypes.Json);
    }

    public async Task<object> Any(GetAnswerBody request)
    {
        var answerFile = await questions.GetAnswerFileAsync(request.Id);
        if (answerFile == null)
            throw HttpError.NotFound("Answer does not exist");

        var json = await answerFile.ReadAllTextAsync();
        if (answerFile.Name.Contains(".a."))
        {
            var obj = (Dictionary<string,object>)JSON.parse(json);
            var choices = (List<object>) obj["choices"];
            var choice = (Dictionary<string,object>)choices[0];
            var message = (Dictionary<string,object>)choice["message"];
            var body = (string)message["content"];
            return new HttpResult(body, MimeTypes.PlainText);
        }
        else
        {
            var answer = json.FromJson<Post>();
            return new HttpResult(answer.Body, MimeTypes.PlainText);
        }
    }

    public async Task Any(CreateWorkerAnswer request)
    {
        var json = request.Json;
        if (string.IsNullOrEmpty(json))
        {
            var file = base.Request!.Files.FirstOrDefault();
            if (file != null)
            {
                using var reader = new StreamReader(file.InputStream);
                json = await reader.ReadToEndAsync();
            }
        }

        json = json?.Trim();
        if (string.IsNullOrEmpty(json))
            throw new ArgumentException("Json is required", nameof(request.Json));
        if (!json.StartsWith('{'))
            throw new ArgumentException("Invalid Json", nameof(request.Json));
        
        rendererCache.DeleteCachedQuestionPostHtml(request.PostId);

        if (request.PostJobId != null)
        {
            MessageProducer.Publish(new DbWrites {
                AnswerAddedToPost = request.PostId,
                CompleteJobIds = [request.PostJobId.Value]
            });
        }
        
        await questions.SaveModelAnswerAsync(request.PostId, request.Model, json);
        
        answerNotifier.NotifyNewAnswer(request.PostId, request.Model);
    }

    public async Task<object> Any(CreateComment request)
    {
        var question = await Db.SingleByIdAsync<Post>(request.Id);
        if (question == null)
            throw HttpError.NotFound($"Question {request.Id} not found");

        if (question.LockedDate != null)
            throw HttpError.Conflict("Question is locked");

        var postId = request.Id.LeftPart('-').ToInt();

        var metaFile = await questions.GetMetaFileAsync(postId);
        var metaJson = metaFile != null
            ? await metaFile.ReadAllTextAsync()
            : "{}";
        
        var meta = metaJson.FromJson<Meta>();
        
        meta.Comments ??= new();
        meta.Comments[request.Id] ??= new();
        meta.Comments[request.Id].Add(new Comment
        {
            Body = request.Body,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = GetUserName(),
        });

        var updatedJson = questions.ToJson(meta);
        await questions.SaveMetaFileAsync(postId, updatedJson);
        
        return new CreateCommentResponse
        {
            Comments = meta.Comments[request.Id]
        };
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

    private string GetUserName()
    {
        var userName = Request.GetClaimsPrincipal().GetUserName()
                       ?? throw new ArgumentNullException(nameof(ApplicationUser.UserName));
        return userName;
    }
}
