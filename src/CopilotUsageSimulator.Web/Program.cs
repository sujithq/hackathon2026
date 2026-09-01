using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using CopilotUsageSimulator.Engine;
using CopilotUsageSimulator.Engine.Configuration;
using CopilotUsageSimulator.Engine.Simulation;
using CopilotUsageSimulator.Web;
using CopilotUsageSimulator.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
var configuration = EngineConfigurationLoader.LoadDefault();
builder.Services.AddSingleton(configuration);
builder.Services.AddSingleton<ICopilotUsageSimulationEngine>(
    new CopilotUsageSimulationEngine(configuration));
builder.Services.AddSingleton<SimulationSessionRunner>();
builder.Services.AddSingleton<ScenarioJson>();

await builder.Build().RunAsync();
