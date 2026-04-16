using Moq;
using SmartPark.Core.Interfaces;
using SmartPark.Core.Models;
using SmartPark.Core.Services;

namespace SmartPark.Tests;

public class ParkingSessionManagerTests
{
    private readonly Mock<IPaymentGateway> _paymentStub = new();
    private readonly Mock<INotificationService> _notificationStub = new();
    private readonly Mock<IMembershipService> _membershipStub = new();
    private readonly Mock<IParkingRepository> _repoStub = new();
    private readonly Mock<IDateTimeProvider> _dateTimeStub = new();
    private readonly ParkingFeeCalculator _feeCalculator = new();
    private readonly ParkingSessionManager _manager;

    public ParkingSessionManagerTests()
    {
        _manager = new ParkingSessionManager(
            _feeCalculator,
            _paymentStub.Object,
            _notificationStub.Object,
            _membershipStub.Object,
            _repoStub.Object,
            _dateTimeStub.Object);
    }

    #region CheckIn — Happy Path

    [Fact]
    public async Task CheckInAsync_NewVehicle_SavesTicketAndReturnIt()
    {
        // Arrange
        _membershipStub.Setup(m => m.GetMembershipTier("PP-1234")).Returns(MembershipTier.Silver);
        _repoStub.Setup(r => r.GetActiveTicketByPlateAsync("PP-1234")).ReturnsAsync((ParkingTicket?)null);
        _dateTimeStub.Setup(d => d.Now).Returns(new DateTime(2026, 3, 16, 10, 0, 0));

        // Act
        var ticket = await _manager.CheckInAsync("PP-1234", VehicleType.Car);

        // Assert
        Assert.Equal("PP-1234", ticket.Vehicle.LicensePlate);
        Assert.Equal(VehicleType.Car, ticket.Vehicle.Type);
        Assert.Equal(MembershipTier.Silver, ticket.Vehicle.Membership);
        Assert.Equal(new DateTime(2026, 3, 16, 10, 0, 0), ticket.CheckInTime);
        Assert.True(ticket.IsActive);

        _membershipStub.Verify(m => m.GetMembershipTier("PP-1234"), Times.Once);
        _repoStub.Verify(r => r.SaveTicketAsync(It.Is<ParkingTicket>(t => t.Vehicle.LicensePlate == "PP-1234")), Times.Once);
    }

    #endregion

    #region CheckIn — Validation

    [Fact]
    public async Task CheckInAsync_DuplicateVehicle_ThrowsAndDoesNotSave()
    {
        // Arrange
        var existingTicket = new ParkingTicket
        {
            Vehicle = new Vehicle { LicensePlate = "PP-9999", Type = VehicleType.Car },
            CheckInTime = DateTime.Now
        };
        _membershipStub.Setup(m => m.GetMembershipTier("PP-9999")).Returns(MembershipTier.Guest);
        _repoStub.Setup(r => r.GetActiveTicketByPlateAsync("PP-9999")).ReturnsAsync(existingTicket);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.CheckInAsync("PP-9999", VehicleType.Car));

        _repoStub.Verify(r => r.SaveTicketAsync(It.IsAny<ParkingTicket>()), Times.Never);
    }

    #endregion

    #region CheckOut — Happy Path

    [Fact]
    public async Task CheckOutAsync_ValidTicket_ProcessesPaymentAndSendsReceipt()
    {
        // Arrange
        var checkInTime = new DateTime(2026, 3, 16, 10, 0, 0);
        var checkOutTime = new DateTime(2026, 3, 16, 12, 30, 0);
        var ticket = new ParkingTicket
        {
            TicketId = "TICKET01",
            Vehicle = new Vehicle { LicensePlate = "PP-1234", Type = VehicleType.Car, Membership = MembershipTier.Guest },
            CheckInTime = checkInTime
        };

        _repoStub.Setup(r => r.GetTicketByIdAsync("TICKET01")).ReturnsAsync(ticket);
        _dateTimeStub.Setup(d => d.Now).Returns(checkOutTime);
        _paymentStub.Setup(p => p.ProcessPaymentAsync("TICKET01", It.IsAny<decimal>())).ReturnsAsync(true);

        // Act
        var result = await _manager.CheckOutAsync("TICKET01", "012-345-678");

        // Assert — Car, 2h billable (2.5h - 30m grace → ceil = 2), 2,000 KHR
        Assert.Equal(2_000m, result.TotalFee);

        _paymentStub.Verify(p => p.ProcessPaymentAsync("TICKET01", 2_000m), Times.Once);
        _repoStub.Verify(r => r.UpdateTicketAsync(ticket), Times.Once);
        _notificationStub.Verify(n => n.SendReceiptAsync("012-345-678", It.IsAny<string>()), Times.Once);
    }

    #endregion

    #region CheckOut — Payment Failure

    [Fact]
    public async Task CheckOutAsync_PaymentFails_ThrowsAndDoesNotUpdateOrNotify()
    {
        // Arrange
        var ticket = new ParkingTicket
        {
            TicketId = "TICKET02",
            Vehicle = new Vehicle { LicensePlate = "PP-5555", Type = VehicleType.Car, Membership = MembershipTier.Guest },
            CheckInTime = new DateTime(2026, 3, 16, 10, 0, 0)
        };

        _repoStub.Setup(r => r.GetTicketByIdAsync("TICKET02")).ReturnsAsync(ticket);
        _dateTimeStub.Setup(d => d.Now).Returns(new DateTime(2026, 3, 16, 13, 0, 0));
        _paymentStub.Setup(p => p.ProcessPaymentAsync("TICKET02", It.IsAny<decimal>())).ReturnsAsync(false);

        // Act & Assert
        await Assert.ThrowsAsync<Exception>(() =>
            _manager.CheckOutAsync("TICKET02", "012-999-888"));

        _repoStub.Verify(r => r.UpdateTicketAsync(It.IsAny<ParkingTicket>()), Times.Never);
        _notificationStub.Verify(n => n.SendReceiptAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.True(ticket.IsActive);
    }

    #endregion

    #region CheckOut — Notification Failure

    [Fact]
    public async Task CheckOutAsync_NotificationFails_CheckoutStillSucceeds()
    {
        // Arrange
        var ticket = new ParkingTicket
        {
            TicketId = "TICKET03",
            Vehicle = new Vehicle { LicensePlate = "PP-7777", Type = VehicleType.Motorcycle, Membership = MembershipTier.Guest },
            CheckInTime = new DateTime(2026, 3, 16, 10, 0, 0)
        };

        _repoStub.Setup(r => r.GetTicketByIdAsync("TICKET03")).ReturnsAsync(ticket);
        _dateTimeStub.Setup(d => d.Now).Returns(new DateTime(2026, 3, 16, 12, 0, 0));
        _paymentStub.Setup(p => p.ProcessPaymentAsync("TICKET03", It.IsAny<decimal>())).ReturnsAsync(true);
        _notificationStub
            .Setup(n => n.SendReceiptAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("SMS gateway down"));

        // Act
        var result = await _manager.CheckOutAsync("TICKET03", "012-111-222");

        // Assert — checkout succeeds despite notification failure (graceful degradation)
        Assert.True(result.TotalFee > 0);
        _paymentStub.Verify(p => p.ProcessPaymentAsync("TICKET03", It.IsAny<decimal>()), Times.Once);
        _repoStub.Verify(r => r.UpdateTicketAsync(ticket), Times.Once);
    }

    #endregion

    #region CheckOut — Validation

    [Fact]
    public async Task CheckOutAsync_TicketNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        _repoStub.Setup(r => r.GetTicketByIdAsync("INVALID")).ReturnsAsync((ParkingTicket?)null);

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _manager.CheckOutAsync("INVALID", "012-000-000"));
    }

    [Fact]
    public async Task CheckOutAsync_TicketAlreadyCheckedOut_ThrowsInvalidOperationException()
    {
        // Arrange
        var ticket = new ParkingTicket
        {
            TicketId = "TICKET04",
            Vehicle = new Vehicle { LicensePlate = "PP-3333", Type = VehicleType.SUV, Membership = MembershipTier.Guest },
            CheckInTime = new DateTime(2026, 3, 16, 10, 0, 0),
            CheckOutTime = new DateTime(2026, 3, 16, 12, 0, 0)
        };
        _repoStub.Setup(r => r.GetTicketByIdAsync("TICKET04")).ReturnsAsync(ticket);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.CheckOutAsync("TICKET04", "012-000-000"));
    }

    #endregion

    #region Verify Interaction Order

    [Fact]
    public async Task CheckOutAsync_InteractionOrder_PaymentBeforeUpdateReceiptAfter()
    {
        // Arrange
        var callOrder = new List<string>();

        var ticket = new ParkingTicket
        {
            TicketId = "TICKET05",
            Vehicle = new Vehicle { LicensePlate = "PP-8888", Type = VehicleType.Car, Membership = MembershipTier.Guest },
            CheckInTime = new DateTime(2026, 3, 16, 10, 0, 0)
        };

        _repoStub.Setup(r => r.GetTicketByIdAsync("TICKET05")).ReturnsAsync(ticket);
        _dateTimeStub.Setup(d => d.Now).Returns(new DateTime(2026, 3, 16, 13, 0, 0));

        _paymentStub
            .Setup(p => p.ProcessPaymentAsync(It.IsAny<string>(), It.IsAny<decimal>()))
            .Callback(() => callOrder.Add("payment"))
            .ReturnsAsync(true);

        _repoStub
            .Setup(r => r.UpdateTicketAsync(It.IsAny<ParkingTicket>()))
            .Callback(() => callOrder.Add("update"))
            .Returns(Task.CompletedTask);

        _notificationStub
            .Setup(n => n.SendReceiptAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Callback(() => callOrder.Add("receipt"))
            .Returns(Task.CompletedTask);

        // Act
        await _manager.CheckOutAsync("TICKET05", "012-555-666");

        // Assert — payment → update → receipt
        Assert.Equal(new[] { "payment", "update", "receipt" }, callOrder);
    }

    #endregion
}
