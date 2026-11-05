using System;
using Xunit;
using Circularity;

namespace Circularity.UnitTests;

public class UsagePingTests
{
    private static readonly DateOnly Today = new(2026, 11, 5);

    [Fact]
    public void IsDue_NeverSent_IsDue() => Assert.True(UsagePing.IsDue(null, Today));

    [Fact]
    public void IsDue_SentToday_IsNotDue() => Assert.False(UsagePing.IsDue("2026-11-05", Today));

    [Fact]
    public void IsDue_SentEarlier_IsDue() => Assert.True(UsagePing.IsDue("2026-11-04", Today));

    [Fact]
    public void IsDue_FutureDate_IsDue() => Assert.True(UsagePing.IsDue("2027-01-01", Today));

    [Fact]
    public void IsDue_Garbage_IsDue() => Assert.True(UsagePing.IsDue("not a date", Today));

    [Fact]
    public void FormatDate_IsYearMonthDay() => Assert.Equal("2026-11-05", UsagePing.FormatDate(Today));
}
