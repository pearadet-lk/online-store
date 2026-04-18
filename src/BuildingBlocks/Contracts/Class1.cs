namespace Contracts;

public sealed record CartItemDto(Guid ProductId, int Quantity, decimal UnitPrice);

public sealed record CheckoutRequest(Guid UserId, string Currency, IReadOnlyList<CartItemDto> Items);

public sealed record CreateOrderRequest(Guid UserId, string Currency, IReadOnlyList<CartItemDto> Items);

public sealed record OrderDto(
    Guid OrderId,
    Guid UserId,
    decimal TotalAmount,
    string Currency,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record AuthorizePaymentRequest(Guid OrderId, decimal Amount, string Currency, string PaymentMethodToken);

public sealed record PaymentDto(
    Guid PaymentId,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string Status,
    string ProviderReference,
    DateTimeOffset CreatedAt);

public sealed record ProductDto(
    Guid ProductId,
    string Name,
    string Description,
    decimal Price,
    bool IsActive);

public sealed record UpsertCartRequest(Guid UserId, IReadOnlyList<CartItemDto> Items);

public sealed record CartDto(Guid CartId, Guid UserId, IReadOnlyList<CartItemDto> Items, DateTimeOffset UpdatedAt);

public sealed record UserProfileDto(Guid UserId, string Email, string FullName, DateTimeOffset CreatedAt);

public sealed record UserRegistrationRequest(string Email, string Password, string FullName);

public sealed record UserLoginRequest(string Email, string Password);

public sealed record InventoryItemDto(Guid ProductId, int AvailableQty, int ReservedQty, DateTimeOffset UpdatedAt);

public sealed record ShipmentDto(
    Guid ShipmentId,
    Guid OrderId,
    string Carrier,
    string TrackingNumber,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record OrderHistoryEventDto(
    Guid HistoryId,
    Guid OrderId,
    Guid UserId,
    string EventType,
    DateTimeOffset CreatedAt,
    string Notes);
