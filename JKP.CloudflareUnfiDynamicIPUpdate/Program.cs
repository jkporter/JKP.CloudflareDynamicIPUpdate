using JKP.CloudflareDynamicIPUpdate;
using JKP.CloudflareDynamicIPUpdate.Configuration;
using Host = Microsoft.Extensions.Hosting.Host;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

builder.Services.Configure<DynamicUpdateConfig>(
    builder.Configuration.GetSection("DynamicUpdate"));

var host = builder.Build();
host.Run();
