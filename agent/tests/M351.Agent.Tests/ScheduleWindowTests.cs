using M351.Agent.Core.Collectors;
using M351.Agent.Core.Contracts;
using Xunit;

namespace M351.Agent.Tests;

public class ScheduleWindowTests
{
    private static readonly CollectionWindow BusinessHours = new()
    {
        Mode = CollectionWindowModes.BusinessHours,
        Days = [1, 2, 3, 4, 5], // seg–sex
        Start = "08:00",
        End = "18:00"
    };

    [Fact]
    public void ALWAYS_sempre_permite()
    {
        var window = new CollectionWindow { Mode = CollectionWindowModes.Always };
        Assert.True(ScheduleWindow.IsCollectionAllowed(window, new DateTime(2026, 6, 7, 3, 0, 0))); // domingo 3h
    }

    [Fact]
    public void BUSINESS_HOURS_permite_em_dia_util_dentro_do_horario()
    {
        Assert.True(ScheduleWindow.IsCollectionAllowed(BusinessHours, new DateTime(2026, 6, 9, 10, 0, 0))); // terça 10h
    }

    [Fact]
    public void BUSINESS_HOURS_bloqueia_fora_do_horario()
    {
        Assert.False(ScheduleWindow.IsCollectionAllowed(BusinessHours, new DateTime(2026, 6, 9, 19, 0, 0))); // terça 19h
        Assert.False(ScheduleWindow.IsCollectionAllowed(BusinessHours, new DateTime(2026, 6, 9, 7, 59, 0)));
    }

    [Fact]
    public void BUSINESS_HOURS_bloqueia_fim_de_semana()
    {
        Assert.False(ScheduleWindow.IsCollectionAllowed(BusinessHours, new DateTime(2026, 6, 7, 10, 0, 0))); // domingo
        Assert.False(ScheduleWindow.IsCollectionAllowed(BusinessHours, new DateTime(2026, 6, 6, 10, 0, 0))); // sábado
    }
}
