using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
    builder.Services.AddSingleton<HttpService>();
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<ItemService>();

builder.Services.AddSingleton<CacheData>();
builder.Services.AddSingleton(new SemaphoreSlim(1, 1));
builder.Services.AddSingleton<LockData>();
builder.Services.AddSingleton<LockService>();





builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins("http://127.0.0.1:5500", "http://localhost:5500")
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors("AllowFrontend");
app.MapControllers();

app.Run();