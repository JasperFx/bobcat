var app = WebApplication.CreateBuilder(args).Build();
app.MapGet("/greeting/{name}", (string name) => Greetings.For(name));
app.Run();

/// <summary>What the specs in Bobcat.Console.SampleWeb.Specs exercise, so the reference is a real one.</summary>
public static class Greetings
{
    public static string For(string name) => $"Hello, {name}";
}
