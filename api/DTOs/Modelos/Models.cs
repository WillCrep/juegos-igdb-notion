namespace FinanceApp.Models;

// Auth
public record RegisterDto(string Email, string Password, string FullName, string? HouseholdName, string? InviteCode);
public record LoginDto(string Email, string Password);
public record AuthenticatedUser(int UserId, string Email, string FullName, int? HouseholdId, string? HouseholdName, string Role);

// Transacciones e Ítems de Boleta
public record TransactionItemDto(
    string ProductName,
    decimal Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    int? CategoryId,
    string? Notes
);

public record TransactionCreateDto(
    int AccountId,
    int? CategoryId,
    decimal Amount,
    string TransactionType,
    DateTime TransactionDate,
    string? Description,
    bool IsHouseholdExpense,
    string? StoreName,
    string? LocationAddress,
    decimal? Latitude,
    decimal? Longitude,
    List<TransactionItemDto>? Items
);

// Cuentas y Categorías
public record AccountCreateDto(string Name, int AccountTypeId, decimal InitialBalance, string Currency, bool IsSharedHousehold);
public record AccountUpdateDto(int AccountId, string Name, int AccountTypeId, string Currency, bool IsSharedHousehold);
public record CategoryCreateDto(string Name, string Type, string? Color);

// Patrimonio / Inventario
public record AssetDto(
    int? AssetId,
    string Name,
    string Category,
    decimal PurchasePrice,
    decimal CurrentEstimatedValue,
    DateTime? PurchaseDate,
    DateTime? WarrantyExpirationDate,
    string? SerialNumber,
    string? RoomLocation,
    string? Notes,
    bool IsSharedHousehold
);

// Suscripciones y Pagos Fijos
public record SubscriptionDto(
    int? SubscriptionId,
    string Name,
    decimal Amount,
    string Currency,
    int DueDay,
    int AccountId,
    int? CategoryId,
    bool IsSharedHousehold
);

// Metas de Ahorro
public record SavingsGoalDto(
    int? GoalId,
    string Name,
    decimal TargetAmount,
    decimal CurrentSavedAmount,
    string Currency,
    DateTime? TargetDate,
    bool IsSharedHousehold
);

public record GoalDepositDto(decimal Amount);