using DocxProcessing;
using DocxProcessing.Api;
using DocxProcessing.Configuration;
using Microsoft.AspNetCore.Http.Features;

DotEnvLoader.Load();

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 21 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 21 * 1024 * 1024);
builder.Services.AddHttpClient<OpenAiStyleEvaluator>();
builder.Services.AddHttpClient<OpenAiStyleAssigner>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
DocumentEndpoints.Map(app);
app.Run();
