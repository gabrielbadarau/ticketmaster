namespace BookingService.Models;

public class Booking
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.InProgress;
    public decimal TotalPrice { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<BookingTicket> BookingTickets { get; set; } = [];
}
