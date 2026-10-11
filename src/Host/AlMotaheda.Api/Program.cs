using AlMotaheda.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddApiHost();

var app = builder.Build();
app.UseApiHost();

app.Run();
