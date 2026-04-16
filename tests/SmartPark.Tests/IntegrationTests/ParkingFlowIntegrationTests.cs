using Moq;
using SmartPark.Core.Interfaces;
using SmartPark.Core.Models;
using SmartPark.Core.Services;

namespace SmartPark.Tests.IntegrationTests;

public class ParkingFlowIntegrationTests
{
    private readonly ParkingFeeCalculator _feeCalculator = new();
    private readonly InMemoryParkingRepository _repository = new();
    private readonly Mock<IPaymentGateway> _paymentStub = new();
    private readonly Mock<INotificationService> _notificationStub = new();
    private readonly Mock<IMembershipService> _membershipStub;
    private readonly ParkingSessionManager _manager;

    private DateTime _currentTime = new(2026, 3, 16, 10, 0, 0);

    public ParkingFlowIntegrationTests()
    {
        var dateTimeStub = new Mock<IDateTimeProvider>();
        dateTimeStub.Setup(d => d.Now).Returns(() => _currentTime);

        _membershipStub = new Mock<IMembershipService>();
        _membershipStub.Setup(m => m.GetMembershipTier(It.IsAny<string>())).Returns(MembershipTier.Guest);

        _paymentStub.Setup(p => p.ProcessPaymentAsync(It.IsAny<string>(), It.IsAny<decimal>()))
            .ReturnsAsync(true);

        _manager = new ParkingSessionManager(
            _feeCalculator,
            _paymentStub.Object,
            _notificationStub.Object,
            _membershipStub.Object,
            _repository,
            dateTimeStub.Object);
    }

    #region Full Parking Flow

    [Fact]
    public async Task FullFlow_CarTwoHours_CalculatesCorrectFee()
    {
        // Arrange — check in at 10:00 AM Monday
        _currentTime = new DateTime(2026, 3, 16, 10, 0, 0);
        var ticket = await _manager.CheckInAsync("CAR-001", VehicleType.Car);

        // Act — check out at 12:30 PM (2.5 hours → 2 billable hours after grace)
        _currentTime = new DateTime(2026, 3, 16, 12, 30, 0);
        var result = await _manager.CheckOutAsync(ticket.TicketId, "012-111-111");

        // Assert — Car: 2 × 1,000 = 2,000 KHR
        Assert.Equal(2_000m, result.TotalFee);
        Assert.False(ticket.IsActive);
    }

    [Fact]
    public async Task FullFlow_MotorcycleGracePeriod_FreeParking()
    {
        // Arrange
        _currentTime = new DateTime(2026, 3, 16, 10, 0, 0);
        var ticket = await _manager.CheckInAsync("MOTO-001", VehicleType.Motorcycle);

        // Act — check out within 20 minutes (grace period)
        _currentTime = new DateTime(2026, 3, 16, 10, 20, 0);
        var result = await _manager.CheckOutAsync(ticket.TicketId, "012-222-222");

        // Assert — within grace period, free
        Assert.Equal(0m, result.TotalFee);
    }

    #endregion

    #region Multiple Vehicles

    [Fact]
    public async Task MultipleVehicles_CheckInThreeCheckOutOne_TwoRemainActive()
    {
        // Arrange — check in 3 vehicles
        _currentTime = new DateTime(2026, 3, 16, 10, 0, 0);
        var ticket1 = await _manager.CheckInAsync("MV-001", VehicleType.Car);
        var ticket2 = await _manager.CheckInAsync("MV-002", VehicleType.SUV);
        var ticket3 = await _manager.CheckInAsync("MV-003", VehicleType.Motorcycle);

        // Act — check out only ticket2
        _currentTime = new DateTime(2026, 3, 16, 12, 0, 0);
        await _manager.CheckOutAsync(ticket2.TicketId, "012-333-333");

        // Assert — 2 remain active
        var activeTickets = await _repository.GetAllActiveTicketsAsync();
        Assert.Equal(2, activeTickets.Count());
        Assert.False(ticket2.IsActive);
        Assert.True(ticket1.IsActive);
        Assert.True(ticket3.IsActive);
    }

    #endregion

    #region Error Recovery

    [Fact]
    public async Task ErrorRecovery_DuplicateCheckIn_ThrowsAndOriginalStaysActive()
    {
        // Arrange
        _currentTime = new DateTime(2026, 3, 16, 10, 0, 0);
        var originalTicket = await _manager.CheckInAsync("DUP-001", VehicleType.Car);

        // Act & Assert — duplicate check-in throws
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.CheckInAsync("DUP-001", VehicleType.Car));

        // Original ticket is still active
        Assert.True(originalTicket.IsActive);
        var activeTickets = await _repository.GetAllActiveTicketsAsync();
        Assert.Single(activeTickets);
    }

    [Fact]
    public async Task ErrorRecovery_PaymentFails_TicketRemainsActive()
    {
        // Arrange
        _currentTime = new DateTime(2026, 3, 16, 10, 0, 0);
        var ticket = await _manager.CheckInAsync("PAY-FAIL", VehicleType.Car);

        _paymentStub
            .Setup(p => p.ProcessPaymentAsync(ticket.TicketId, It.IsAny<decimal>()))
            .ReturnsAsync(false);

        // Act — payment fails
        _currentTime = new DateTime(2026, 3, 16, 13, 0, 0);
        await Assert.ThrowsAsync<Exception>(() =>
            _manager.CheckOutAsync(ticket.TicketId, "012-444-444"));

        // Assert — ticket is still active (not checked out)
        Assert.True(ticket.IsActive);
        Assert.Null(ticket.CheckOutTime);
    }

    #endregion

    #region Edge-to-Edge Scenarios

    [Fact]
    public async Task EdgeToEdge_OvernightWeekendGoldMember_CalculatesCorrectFee()
    {
        // Arrange — Gold member on Saturday evening
        _membershipStub.Setup(m => m.GetMembershipTier("GOLD-SAT")).Returns(MembershipTier.Gold);

        _currentTime = new DateTime(2026, 3, 21, 20, 0, 0); // Saturday 8 PM
        var ticket = await _manager.CheckInAsync("GOLD-SAT", VehicleType.Car);

        // Act — check out Sunday 1 AM (5 hours, overnight, weekend)
        _currentTime = new DateTime(2026, 3, 22, 1, 0, 0);

        var result = await _manager.CheckOutAsync(ticket.TicketId, "012-555-555");

        // 5h total - 30m grace = 4.5h → ceil = 5 billable hours → 5,000 base
        // Weekend surcharge: 5,000 × 20% = 1,000
        // Gold discount: (5,000 + 1,000) × 25% = 1,500
        // Overnight: 2,000
        // Total: 5,000 + 1,000 - 1,500 + 2,000 = 6,500
        Assert.Equal(5_000m, result.BaseFee);
        Assert.Equal(1_000m, result.SurchargeAmount);
        Assert.Equal(1_500m, result.DiscountAmount);
        Assert.Equal(6_500m, result.TotalFee);
    }

    [Fact]
    public async Task EdgeToEdge_LostTicketDuringGracePeriod_OnlyPenalty()
    {
        // Arrange
        _currentTime = new DateTime(2026, 3, 16, 10, 0, 0);
        var ticket = await _manager.CheckInAsync("LOST-GP", VehicleType.SUV);

        // Act — check out within 10 minutes with lost ticket
        _currentTime = new DateTime(2026, 3, 16, 10, 10, 0);
        var result = await _manager.CheckOutAsync(ticket.TicketId, "012-666-666", isLostTicket: true);

        // Assert — 0 base + 20,000 penalty
        Assert.Equal(0m, result.BaseFee);
        Assert.Equal(20_000m, result.LostTicketPenalty);
        Assert.Equal(20_000m, result.TotalFee);
    }

    [Fact]
    public async Task EdgeToEdge_SuvDailyCap_FeeNeverExceedsCap()
    {
        // Arrange — SUV parked for 24 hours on a weekday
        _currentTime = new DateTime(2026, 3, 16, 8, 0, 0); // Monday
        var ticket = await _manager.CheckInAsync("SUV-CAP", VehicleType.SUV);

        // Act — check out after 24 hours
        _currentTime = new DateTime(2026, 3, 17, 8, 0, 0);
        var result = await _manager.CheckOutAsync(ticket.TicketId, "012-777-777");

        // Assert — capped at 12,000 + 2,000 overnight
        Assert.Equal(12_000m, result.BaseFee);
        Assert.Equal(14_000m, result.TotalFee);
    }

    #endregion
}
