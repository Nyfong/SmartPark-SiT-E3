using SmartPark.Core.Models;
using SmartPark.Core.Services;
using FsCheck;
using FsCheck.Xunit;

namespace SmartPark.Tests;

public class ParkingFeeCalculatorTests
{
    private readonly ParkingFeeCalculator _calculator = new();

    #region Edge Cases

    [Fact]
    public void CalculateFee_CheckOutBeforeCheckIn_ThrowsArgumentException()
    {
        // Arrange
        var checkIn = new DateTime(2026, 3, 16, 10, 0, 0);
        var checkOut = new DateTime(2026, 3, 16, 9, 0, 0);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            _calculator.CalculateFee(VehicleType.Car, MembershipTier.Guest, checkIn, checkOut));
    }

    [Fact]
    public void CalculateFee_ZeroDuration_ReturnsFree()
    {
        // Arrange
        var checkIn = new DateTime(2026, 3, 16, 10, 0, 0);
        var checkOut = checkIn;

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, MembershipTier.Guest, checkIn, checkOut);

        // Assert
        Assert.Equal(0m, result.TotalFee);
    }

    #endregion

    #region Grace Period

    [Theory]
    [InlineData(0, 0)]
    [InlineData(15, 0)]
    [InlineData(29, 0)]
    [InlineData(30, 0)]
    public void CalculateFee_GracePeriod_WithinWindow_ReturnsFree(int minutes, decimal expected)
    {
        // Arrange
        var checkIn = new DateTime(2026, 3, 16, 10, 0, 0);
        var checkOut = checkIn.AddMinutes(minutes);

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, MembershipTier.Guest, checkIn, checkOut);

        // Assert
        Assert.Equal(expected, result.TotalFee);
    }

    [Fact]
    public void CalculateFee_GracePeriod_31Minutes_ChargesOneHour()
    {
        // Arrange
        var checkIn = new DateTime(2026, 3, 16, 10, 0, 0);
        var checkOut = checkIn.AddMinutes(31);

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, MembershipTier.Guest, checkIn, checkOut);

        // Assert
        Assert.Equal(1_000m, result.TotalFee);
    }

    #endregion

    #region Basic Fee Calculation

    [Theory]
    [InlineData(VehicleType.Motorcycle, 2, 1_000)]
    [InlineData(VehicleType.Car, 3, 3_000)]
    [InlineData(VehicleType.SUV, 1, 1_500)]
    public void CalculateFee_BasicRate_ReturnsCorrectFee(VehicleType type, int hoursAfterGrace, decimal expected)
    {
        // Arrange
        var checkIn = new DateTime(2026, 3, 16, 10, 0, 0);
        var checkOut = checkIn.AddMinutes(GracePeriodMinutes + (hoursAfterGrace * 60));

        // Act
        var result = _calculator.CalculateFee(type, MembershipTier.Guest, checkIn, checkOut);

        // Assert
        Assert.Equal(expected, result.BaseFee);
        Assert.Equal(expected, result.TotalFee);
    }

    #endregion

    private const int GracePeriodMinutes = 30;

    #region Duration Rounding
    #endregion

    #region Daily Cap

    [Theory]
    [InlineData(VehicleType.Motorcycle, 10, 4_000)]
    [InlineData(VehicleType.Car, 12, 8_000)]
    [InlineData(VehicleType.SUV, 24, 12_000)]
    public void CalculateFee_DailyCap_FeeNeverExceedsCap(VehicleType type, int hours, decimal expectedCap)
    {
        // Arrange
        var checkIn = new DateTime(2026, 3, 16, 6, 0, 0);
        var checkOut = checkIn.AddHours(hours);

        // Act
        var result = _calculator.CalculateFee(type, MembershipTier.Guest, checkIn, checkOut);

        // Assert
        Assert.Equal(expectedCap, result.BaseFee);
    }

    #endregion

    #region Overnight Fee

    [Fact]
    public void CalculateFee_Overnight_PastTenPM_AddsOvernightFee()
    {
        // Arrange — check in 8 PM, check out 11 PM (spans past 10 PM)
        var checkIn = new DateTime(2026, 3, 16, 20, 0, 0);
        var checkOut = new DateTime(2026, 3, 16, 23, 0, 0);

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, MembershipTier.Guest, checkIn, checkOut);

        // Assert — 3h total - 30m grace = 2.5h → ceil(3) billable → 3,000 base + 2,000 overnight
        Assert.Equal(3_000m, result.BaseFee);
        Assert.Equal(5_000m, result.TotalFee);
    }

    [Fact]
    public void CalculateFee_Overnight_CheckInAfterTenPM_AddsOvernightFee()
    {
        // Arrange — check in 11 PM, check out 6 AM next day
        var checkIn = new DateTime(2026, 3, 16, 23, 0, 0);
        var checkOut = new DateTime(2026, 3, 17, 6, 0, 0);

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, MembershipTier.Guest, checkIn, checkOut);

        // Assert — overnight fee applied
        Assert.True(result.TotalFee > result.BaseFee);
    }

    [Fact]
    public void CalculateFee_Overnight_NoOvernightBeforeTenPM_NoFee()
    {
        // Arrange — check in 8 AM, check out 5 PM (no overnight)
        var checkIn = new DateTime(2026, 3, 16, 8, 0, 0);
        var checkOut = new DateTime(2026, 3, 16, 17, 0, 0);

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, MembershipTier.Guest, checkIn, checkOut);

        // Assert — capped at 8,000, no overnight
        Assert.Equal(8_000m, result.TotalFee);
        Assert.Equal(result.BaseFee, result.TotalFee);
    }

    #endregion

    #region Weekend Surcharge

    [Fact]
    public void CalculateFee_Weekend_Saturday_Adds20PercentSurcharge()
    {
        // Arrange — Saturday, 2 hours total (1.5h past grace → 2 billable → wait no)
        // 2h total = 120 min. 120-30=90, ceil(90/60)=2 hours. Car: 2*1000=2000. 20% of 2000=400.
        var checkIn = new DateTime(2026, 3, 21, 10, 0, 0); // Saturday
        var checkOut = checkIn.AddHours(2).AddMinutes(30); // 2.5 hours

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, MembershipTier.Guest, checkIn, checkOut);

        // Assert — 2.5h total - 30m grace = 2h → 2,000 base + 400 surcharge = 2,400
        Assert.Equal(2_000m, result.BaseFee);
        Assert.Equal(400m, result.SurchargeAmount);
        Assert.Equal(2_400m, result.TotalFee);
    }

    [Fact]
    public void CalculateFee_Weekend_Sunday_Adds20PercentSurcharge()
    {
        // Arrange — Sunday, motorcycle 1h past grace
        var checkIn = new DateTime(2026, 3, 22, 10, 0, 0); // Sunday
        var checkOut = checkIn.AddMinutes(GracePeriodMinutes + 60);

        // Act
        var result = _calculator.CalculateFee(VehicleType.Motorcycle, MembershipTier.Guest, checkIn, checkOut);

        // Assert — 500 base + 100 surcharge = 600
        Assert.Equal(500m, result.BaseFee);
        Assert.Equal(100m, result.SurchargeAmount);
        Assert.Equal(600m, result.TotalFee);
    }

    [Fact]
    public void CalculateFee_Weekday_NoSurcharge()
    {
        // Arrange — Monday, car 2 hours past grace
        var checkIn = new DateTime(2026, 3, 16, 10, 0, 0); // Monday
        var checkOut = checkIn.AddMinutes(GracePeriodMinutes + 120);

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, MembershipTier.Guest, checkIn, checkOut);

        // Assert — 2,000 base, no surcharge
        Assert.Equal(2_000m, result.BaseFee);
        Assert.Equal(0m, result.SurchargeAmount);
        Assert.Equal(2_000m, result.TotalFee);
    }

    #endregion

    #region Holiday Surcharge

    [Fact]
    public void CalculateFee_Holiday_Adds50PercentSurcharge()
    {
        // Arrange — holiday weekday, car 2h past grace
        var checkIn = new DateTime(2026, 3, 16, 10, 0, 0); // Monday
        var checkOut = checkIn.AddMinutes(GracePeriodMinutes + 120);

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, MembershipTier.Guest, checkIn, checkOut, isHoliday: true);

        // Assert — 2,000 base + 1,000 surcharge = 3,000
        Assert.Equal(2_000m, result.BaseFee);
        Assert.Equal(1_000m, result.SurchargeAmount);
        Assert.Equal(3_000m, result.TotalFee);
    }

    [Fact]
    public void CalculateFee_HolidayOnWeekend_HolidayTakesPriority()
    {
        // Arrange — holiday on Saturday, car 2h past grace
        var checkIn = new DateTime(2026, 3, 21, 10, 0, 0); // Saturday
        var checkOut = checkIn.AddMinutes(GracePeriodMinutes + 120);

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, MembershipTier.Guest, checkIn, checkOut, isHoliday: true);

        // Assert — holiday 50% takes priority over weekend 20%: 2,000 + 1,000 = 3,000
        Assert.Equal(1_000m, result.SurchargeAmount);
        Assert.Equal(3_000m, result.TotalFee);
    }

    #endregion

    #region Membership Discounts

    [Theory]
    [InlineData(MembershipTier.Guest, 2_000)]
    [InlineData(MembershipTier.Silver, 1_800)]
    [InlineData(MembershipTier.Gold, 1_500)]
    [InlineData(MembershipTier.Platinum, 1_200)]
    public void CalculateFee_MembershipDiscount_AppliedCorrectly(MembershipTier tier, decimal expectedTotal)
    {
        // Arrange — Car, 2 hours past grace on a weekday
        var checkIn = new DateTime(2026, 3, 16, 10, 0, 0); // Monday
        var checkOut = checkIn.AddMinutes(GracePeriodMinutes + 120);

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, tier, checkIn, checkOut);

        // Assert
        Assert.Equal(2_000m, result.BaseFee);
        Assert.Equal(expectedTotal, result.TotalFee);
    }

    [Fact]
    public void CalculateFee_MembershipDiscount_AppliedToBasePlusSurcharge()
    {
        // Arrange — Gold member, Saturday, car 2h past grace
        var checkIn = new DateTime(2026, 3, 21, 10, 0, 0); // Saturday
        var checkOut = checkIn.AddMinutes(GracePeriodMinutes + 120);

        // Act
        var result = _calculator.CalculateFee(VehicleType.Car, MembershipTier.Gold, checkIn, checkOut);

        // Assert — base=2000, surcharge=400, discount=25% of (2000+400)=600, total=2000+400-600=1800
        Assert.Equal(2_000m, result.BaseFee);
        Assert.Equal(400m, result.SurchargeAmount);
        Assert.Equal(600m, result.DiscountAmount);
        Assert.Equal(1_800m, result.TotalFee);
    }

    #endregion

    #region Lost Ticket
    #endregion

    #region Property-Based Tests
    #endregion
}
