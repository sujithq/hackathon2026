using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotUsageSimulator.Engine.Configuration;

namespace CopilotUsageSimulator.Engine.Tests;

public sealed class EngineConfigurationValidationTests
{
    [Theory]
    [InlineData("42")]
    [InlineData("\"unsupported\"")]
    public void LoaderRejectsUnsupportedEnumValues(string value)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var json = JsonSerializer.Serialize(EngineConfigurationLoader.LoadDefault(), options)
            .Replace(
                "\"poolOverflowBehavior\":\"split\"",
                $"\"poolOverflowBehavior\":{value}",
                StringComparison.Ordinal);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        Assert.Throws<JsonException>(() => EngineConfigurationLoader.Load(stream));
    }

    [Fact]
    public void ValidatorRejectsUndefinedPoolOverflowBehavior()
    {
        var configuration = EngineConfigurationLoader.LoadDefault() with
        {
            PoolOverflowBehavior = (PoolOverflowBehavior)42
        };

        var exception = Assert.Throws<ConfigurationException>(
            () => EngineConfigurationValidator.Validate(configuration));

        Assert.Contains("poolOverflowBehavior", exception.Message);
    }

    [Fact]
    public void ValidatorRejectsUndefinedActionsMeteringMode()
    {
        var configuration = EngineConfigurationLoader.LoadDefault();
        configuration = configuration with
        {
            Operations =
            [
                configuration.Operations[0] with
                {
                    ActionsMetering = (ActionsMeteringMode)42
                },
                .. configuration.Operations.Skip(1)
            ]
        };

        var exception = Assert.Throws<ConfigurationException>(
            () => EngineConfigurationValidator.Validate(configuration));

        Assert.Contains("actionsMetering", exception.Message);
    }
}