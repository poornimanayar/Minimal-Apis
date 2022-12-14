//create a web application builder
//builder is responsible for services, logging, configuration

var builder = WebApplication.CreateBuilder();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options => options.AddPolicy("MyApi", builder => builder.AllowAnyHeader().AllowAnyOrigin().AllowAnyMethod()));

builder.Services.AddSingleton<PersonRepository>();
builder.Services.AddOutputCache(cacheOptions =>
{
    //add a base policy - cache expires in 10s
    //base policy will be used for caching unless a policy is explicitly specified
    cacheOptions.AddBasePolicy(builder => builder.Expire(TimeSpan.FromSeconds(20)).Tag("person"));

    //define policies
    cacheOptions.AddPolicy("Expire20", builder => builder.Expire(TimeSpan.FromSeconds(20)));
    cacheOptions.AddPolicy("Expire30", builder => builder.Expire(TimeSpan.FromSeconds(30)));

    //define policy to vary cache by route param
    cacheOptions.AddPolicy("VaryByRouteParam", builder => builder.SetVaryByRouteValue(new[] { "id" }).Expire(TimeSpan.FromSeconds(30)).Tag("person"));
});

builder.Services.AddRateLimiter(limiterOptions =>
{
    //fixed window rate limiting options
    limiterOptions.AddFixedWindowLimiter(policyName: "myfixedwindowlimit", options =>
        {
            options.PermitLimit = 5; //maximum number of requests permitted
            options.Window = TimeSpan.FromSeconds(10); //time window
            options.QueueLimit = 0;//requests after the limit gets queued in this example, can be set to 0 to avoid queuing
            options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst; //processing order for queues
        });


    //sliding window rate limiting options
    limiterOptions.AddSlidingWindowLimiter(policyName: "myslidingwindowlimit", options =>
      {
          options.PermitLimit = 4;//10 requests for the entire window
          options.Window = TimeSpan.FromSeconds(20);
          options.SegmentsPerWindow = 4; //no of segments in the 20s window
          options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
          options.QueueLimit = 0;
      });

    //sliding window rate limiting options
    limiterOptions.AddTokenBucketLimiter(policyName: "mytokenbucketlimit", options =>
    {
        options.TokenLimit = 10; //max number of tokens in the bucket, cannot exceed this limit while replenishing
        options.ReplenishmentPeriod = TimeSpan.FromSeconds(20); //how often tokens are replenished (autoreplenishment must be true)
        options.TokensPerPeriod = 4; //max number of tokens added to the token bucket per period
        options.AutoReplenishment = true; // whether tokens are replenished automatically
        options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        options.QueueLimit = 3;
    });

    //concurrent rate limit options
    limiterOptions.AddConcurrencyLimiter(policyName: "myconcurrencylimit", options =>
        {
            options.PermitLimit = 10; // max number of concurrent requests at any point of time
            options.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            options.QueueLimit = 2;
        });

    //handler for callback when limit is reached
    // can be used with fixed window, sliding window and token bucket rate limiting
    //queue limit must be set to 0 for requests to ve rejected
    limiterOptions.OnRejected = (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return new ValueTask();
    };
});


//get a webapplication
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

app.UseOutputCache();

app.UseRateLimiter();

//Extension methods of the format Map<HTTPMETHOD> on the IEndpointRouteBuilder interface that WebApplication implements
//new routing apis, WebApplicationBuilder wraps the UseRouting and UseEndpoints 
//connects the delegates/handlers to the routing system
app.MapGet("/hello-minimal-api", () => "Hello Minimal Api!!!");
app.MapPost("/hello-minimal-api", () => "Post to Hello Minimal Api!!!");
app.MapPut("/hello-minimal-api", () => "Put to Hello Minimal Api!!!");
app.MapDelete("/hello-minimal-api", () => "Delete to Hello Minimal Api!!!");

app.MapMethods("/hello-minimal-api", new[] { "OPTIONS" }, () => "This is a OPTIONS call to minimal api");

var handler = () => "This is a variable";
app.MapGet("/hello-minimal-api/lambda-variable", handler);
string LocalFunction() => "This is a function call";
app.MapGet("/hello-minimal-api/local-function", LocalFunction);

app.MapGet("/hello-minimal-api/instance-method", new MinimalApiHandler().InstanceMethod);

app.MapGet("/hello-minimal-api/static-method", MinimalApiHandler.StaticMethod);

//inferred parameter binding from the services in DI container
app.MapGet("/person", (PersonRepository repository) =>
{
    return TypedResults.Ok<List<Person>>(repository.GetAll());
}).CacheOutput();//base policy applied unless a policy is explicitly specified;
//.RequireRateLimiting("myfixedwindowlimit") 

//inferred parameter binding from route values
//.NET 6.0 returns an IResult
//.NET 7.0 returns a implementation of IResults using the TypedResults static class
// gives metadata to OpenAPI to describe endpoint
//better for testing
//Generic Union Types for return types - preserves response metadata, compile time checking for return types from handlers
app.MapGet("/person/{id:int}", Results<NotFound, Ok<Person>> (int id, PersonRepository repository) =>
{
    var person = repository.GetById(id);
    return person is not null ? TypedResults.Ok(person) : TypedResults.NotFound();
}).CacheOutput("VaryByRouteParam");

//create a person and return the url of the newly created resource in location header
//implicit parameter binding maps request body to the person object
app.MapPost("/person", (Person person, PersonRepository repository, IOutputCacheStore cacheStore) =>
{
    repository.Create(person);
    cacheStore.EvictByTagAsync("person", default);
    return TypedResults.Created($"/person/{person.Id}", person);
});

app.MapPut("/person/{id:int}", Results<NotFound, Ok> (int id, Person person, PersonRepository repository, IOutputCacheStore cacheStore) =>
{
    var existingPerson = repository.GetById(id);
    if (existingPerson is null)
    {
        return TypedResults.NotFound();
    }
    repository.Update(existingPerson);
    cacheStore.EvictByTagAsync("person", default);
    return TypedResults.Ok();
});

app.MapDelete("/person/{id:int}", Results<NotFound, NoContent> (int id, PersonRepository repository, IOutputCacheStore cacheStore) =>
{
    var person = repository.GetById(id);
    if (person is null)
    {
        return TypedResults.NotFound();
    }
    repository.Delete(id);
    cacheStore.EvictByTagAsync("person", default);
    return TypedResults.NoContent();
});

//.NET7 feature - upload files using IFormFile/IFormFileCollection
app.MapPost("/person/upload/{id:int}", async (int id, IFormFile imageFile, PersonRepository repository) =>
{
    await imageFile.CopyToAsync(File.OpenWrite(imageFile.FileName));

    //TODO : update person with image
});

//.NET 7 bind arrays
app.MapGet("/person/filterbyid", (int[] ids, PersonRepository repository) =>
{
    return repository.GetByIds(ids);
}).AddEndpointFilter(async (invocationContext, next) =>
{
    app.Logger.LogInformation("==========================================");
    app.Logger.LogInformation($"PATH - {invocationContext.HttpContext.Request.Path}");
    app.Logger.LogInformation($"METHOD - {invocationContext.HttpContext.Request.Method}");
    int[] ids = invocationContext.GetArgument<int[]>(0);

    foreach (var id in ids)
    {
        app.Logger.LogInformation($"{id}");
    }
    foreach (var header in invocationContext.HttpContext.Request.Headers)
    {
        app.Logger.LogInformation($"{header.Key} - {header.Value}");
    }
    app.Logger.LogInformation("==========================================");

    //invoke next filter, or the handler if last filter has been invoked
    return await next.Invoke(invocationContext);
});

//.NET 7 filters - shortcircuit using filters
app.MapGet("/person/{name}", (string name, PersonRepository repository) =>
{
    return TypedResults.Ok(name);
}).AddEndpointFilter(async (invocationcontext, next) =>
{

    var name = invocationcontext.HttpContext.GetRouteValue("name");
    if (string.Equals(name?.ToString(), "Voldemort", StringComparison.InvariantCultureIgnoreCase))
    {
        return "Death Eaters are here..Watch out!!!!!!";
    }

    return await next.Invoke(invocationcontext);
});

app.Run();

public class MinimalApiHandler
{
    public string InstanceMethod()
    {
        return "Hello from instance method";
    }

    public static string StaticMethod()
    {
        return "Hello from staic method";
    }
}

public class LoggingFilter : IEndpointFilter
{
    private readonly ILogger _logger;

    public LoggingFilter(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<LoggingFilter>();
    }
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocationContext, EndpointFilterDelegate next)
    {
        _logger.LogInformation("==========================================");
        _logger.LogInformation($"PATH - {invocationContext.HttpContext.Request.Path}");
        foreach (var header in invocationContext.HttpContext.Request.Headers)
        {
            _logger.LogInformation($"{header.Key} - {header.Value}");
        }
        _logger.LogInformation("==========================================");

        //invoke next filter, or the handler if last filter has been invoked
        return await next.Invoke(invocationContext);
    }
}