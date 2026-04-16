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
    #endregion

    #region Overnight Fee
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
