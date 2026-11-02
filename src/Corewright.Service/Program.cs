using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Corewright.Core;
using Corewright.Service;
using Microsoft.Extensions.Hosting.WindowsServices;
using Monitor = Corewright.Service.Monitor;

bool isService = WindowsServiceHelpers.IsWindowsService();

var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = isService ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = "Corewright");

var options = new ServiceOptions();
builder.Configuration.GetSection("Corewright").Bind(options);
string dataDir = options.DataDirectory ?? (isService ? AppPaths.ServiceDataDirectory : AppPaths.UserDataDirectory);
Directory.CreateDirectory(dataDir);

builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Loopback, options.Port);
    k.AddServerHeader = false;
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJson.Default));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<EventBuffer>();
builder.Services.AddSingleton(sp => new RuleStore(dataDir, sp.GetRequiredService<ILogger<RuleStore>>()));
builder.Services.AddSingleton(sp => new SettingsStore(dataDir, sp.GetRequiredService<ILogger<SettingsStore>>()));
builder.Services.AddSingleton(sp => new Monitor(options, sp.GetRequiredService<RuleStore>(), sp.GetRequiredService<SettingsStore>(),
    sp.GetRequiredService<EventBuffer>(), sp.GetRequiredService<ILogger<Monitor>>(), dataDir) { IsWindowsService = isService, DataDirectory = dataDir });
builder.Services.AddHostedService(sp => sp.GetRequiredService<Monitor>());

var app = builder.Build();
var monitor = app.Services.GetRequiredService<Monitor>();

// A fresh token per run; never logged.
string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
app.Use(Api.Guard(monitor, Encoding.UTF8.GetBytes(token), options.Port));
app.UseDefaultFiles();
// The start page must always be re-checked, or the window keeps showing an old UI after an update (beta 2 did,
// 2026-11-02). Asset files carry a content hash in their name, so they may be cached for good.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        bool hashed = ctx.File.Name.Contains('-') && ctx.Context.Request.Path.StartsWithSegments("/assets");
        ctx.Context.Response.Headers.CacheControl = hashed ? "public, max-age=31536000, immutable" : "no-cache";
    },
});
Api.Map(app, monitor);

string endpointPath = Path.Combine(dataDir, EndpointInfo.FileName);
app.Lifetime.ApplicationStarted.Register(() =>
{
    var info = new EndpointInfo
    {
        Url = $"http://127.0.0.1:{options.Port}",
        Token = token,
        Pid = Environment.ProcessId,
        Mode = isService ? "service" : "console",
    };
    var tmp = endpointPath + ".tmp";
    File.WriteAllText(tmp, JsonSerializer.Serialize(info, CoreJson.Default.EndpointInfo));
    File.Move(tmp, endpointPath, overwrite: true);
    app.Logger.LogInformation("API listening on {Url}; endpoint file {Path}", info.Url, endpointPath);
});
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { File.Delete(endpointPath); } catch (IOException) { }
});

app.Run();
