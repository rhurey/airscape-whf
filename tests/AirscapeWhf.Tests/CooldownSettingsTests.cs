using AirscapeWhf.Automation;

namespace AirscapeWhf.Tests;

public sealed class CooldownSettingsTests
{
    private static readonly CooldownSettings Rules = new()
    {
        Enabled = true,
        Steps = new[] { new CooldownStep(66, 2), new CooldownStep(60, 0) },
    };

    [Fact]
    public void Forecast_names_the_warmest_step_still_ahead_of_the_house_temperature()
    {
        var next = Rules.Forecast(houseF: 69, speed: 5, latched: new HashSet<int>());
        Assert.Equal(new CooldownStep(66, 2), next);
    }

    [Fact]
    public void Forecast_skips_latched_steps_and_steps_that_would_not_slow_the_fan()
    {
        Assert.Equal(new CooldownStep(60, 0), Rules.Forecast(69, 5, new HashSet<int> { 66 }));
        Assert.Equal(new CooldownStep(60, 0), Rules.Forecast(69, 2, new HashSet<int>()));
    }

    [Fact]
    public void Forecast_is_empty_without_a_house_reading_or_a_running_fan()
    {
        Assert.Null(Rules.Forecast(null, 5, new HashSet<int>()));
        Assert.Null(Rules.Forecast(69, 0, new HashSet<int>()));
    }
}
