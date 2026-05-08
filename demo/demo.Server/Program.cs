var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddValidation();

var app = builder.Build();

app.UseStatusCodePages();
app.UseExceptionHandler();
app.UseFileServer();

var api = app.MapGroup("/api");

api.MapGet("weatherforecast", WeatherForecast.Get)
    .WithName("GetWeatherForecast");

app.MapOpenApi();
app.MapDefaultEndpoints();

app.Run();

/// <summary>
/// Represents a weather forecast for a specific date, including temperature in Celsius and Fahrenheit, and a summary of the weather conditions.
/// </summary>
/// <param name="Date">The date of the weather forecast.</param>
/// <param name="TemperatureC">The temperature in Celsius.</param>
/// <param name="Summary">A summary of the weather conditions.</param>
record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);

    /// <summary>
    /// Generates a random weather forecast for the next 5 days.
    /// </summary>
    /// <remarks>
    /// This method creates a random weather forecast for the next 5 days. It uses a predefined set of weather summaries and random temperature values to generate the forecast data.
    /// </remarks>
    /// <response code="200">Returns an array of weather forecasts for the next 5 days.</response>
    /// <response code="500">If there was an error generating the weather forecast.</response>
    /// <returns>A list of weather forecasts.</returns>
    /// <example>
    /// <code>
    /// var forecast = GetWeatherForecast();
    /// foreach (var day in forecast)    /// {
    ///     Console.WriteLine($"{day.Date}: {day.Summary} with a temperature of {day.TemperatureC}°C ({day.TemperatureF}°F)");
    /// }
    /// </code>
    /// </example>
    public static WeatherForecast[] Get()
    {
        string[] summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];

        var forecast = Enumerable.Range(1, 5).Select(index =>
            new WeatherForecast
            (
                DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                Random.Shared.Next(-20, 55),
                summaries[Random.Shared.Next(summaries.Length)]
            ))
            .ToArray();

        return forecast;
    }
}