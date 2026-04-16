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
    #endregion

    #region Holiday Surcharge
    #endregion

    #region Membership Discounts
    #endregion

    #region Lost Ticket
    #endregion

    #region Property-Based Tests
    #endregion
}
