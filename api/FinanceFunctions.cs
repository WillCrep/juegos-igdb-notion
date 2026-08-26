using System.Data;
using System.Net;
using System.Text.Json;
using FinanceApp.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;

namespace FinanceApp;

public class FinanceFunctions
{
    private readonly string _connectionString;

    public FinanceFunctions()
    {
        _connectionString = Environment.GetEnvironmentVariable("SQL_CONNECTION_STRING") ?? string.Empty;
    }

    // =========================================================================
    // 1. CARGA INICIAL & DASHBOARD (KPIs, Cuentas, Activos, Suscripciones)
    // =========================================================================
    [Function("GetInitialData")]
    public async Task<HttpResponseData> GetInitialData([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "finance/init")] HttpRequestData req)
    {
         var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        decimal liquidBalance = 0, creditCardDebt = 0, monthlyIncome = 0, monthlyExpense = 0, householdExpenseThisMonth = 0, totalAssetsValue = 0;

        // 1.1 Saldo Líquido
        var qLiquid = @"
            SELECT ISNULL(SUM(a.CurrentBalance), 0) 
            FROM BnkAccounts a
            INNER JOIN BnkAccountTypes t ON a.AccountTypeId = t.AccountTypeId
            WHERE a.UserId = @UserId AND a.IsActive = 1 AND t.Name NOT LIKE '%Crédito%'";
        using (var cmd = new SqlCommand(qLiquid, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            liquidBalance = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
        }

        // 1.2 Deuda Tarjetas
        var qDebt = @"
            SELECT ISNULL(SUM(t.Amount), 0)
            FROM BnkTransactions t
            INNER JOIN BnkAccounts a ON t.AccountId = a.AccountId
            INNER JOIN BnkAccountTypes at ON a.AccountTypeId = at.AccountTypeId
            WHERE t.UserId = @UserId AND at.Name LIKE '%Crédito%' AND t.TransactionType = 'EXPENSE'";
        using (var cmd = new SqlCommand(qDebt, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            creditCardDebt = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
        }

        // 1.3 Ingresos / Gastos Mes
        var qTotals = @"
            SELECT 
                ISNULL(SUM(CASE WHEN TransactionType = 'INCOME' THEN Amount ELSE 0 END), 0) AS Income,
                ISNULL(SUM(CASE WHEN TransactionType = 'EXPENSE' THEN Amount ELSE 0 END), 0) AS Expense
            FROM BnkTransactions 
            WHERE UserId = @UserId 
              AND MONTH(TransactionDate) = MONTH(GETUTCDATE()) 
              AND YEAR(TransactionDate) = YEAR(GETUTCDATE())";
        using (var cmd = new SqlCommand(qTotals, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                monthlyIncome = reader.GetDecimal(0);
                monthlyExpense = reader.GetDecimal(1);
            }
        }

        // 1.4 Gastos del Hogar Mes
        if (authUser.HouseholdId.HasValue)
        {
            var qHome = @"
                SELECT ISNULL(SUM(Amount), 0) 
                FROM BnkTransactions 
                WHERE HouseholdId = @HouseholdId 
                  AND TransactionType = 'EXPENSE'
                  AND MONTH(TransactionDate) = MONTH(GETUTCDATE()) 
                  AND YEAR(TransactionDate) = YEAR(GETUTCDATE())";
            using var cmd = new SqlCommand(qHome, conn);
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = authUser.HouseholdId.Value;
            householdExpenseThisMonth = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
        }

        // 1.5 Valor del Patrimonio / Activos
        var qAssetsVal = @"
            SELECT ISNULL(SUM(CurrentEstimatedValue), 0) 
            FROM BnkAssets 
            WHERE UserId = @UserId OR (@HouseholdId IS NOT NULL AND HouseholdId = @HouseholdId)";
        using (var cmd = new SqlCommand(qAssetsVal, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)authUser.HouseholdId ?? DBNull.Value;
            totalAssetsValue = Convert.ToDecimal(await cmd.ExecuteScalarAsync());
        }

        // 1.6 Cuentas
        var accounts = new List<object>();
        var qAccounts = @"
            SELECT a.AccountId, a.Name, a.CurrentBalance, a.Currency, t.Name AS AccountType, a.HouseholdId, a.AccountTypeId
            FROM BnkAccounts a
            INNER JOIN BnkAccountTypes t ON a.AccountTypeId = t.AccountTypeId
            WHERE a.IsActive = 1 AND (a.UserId = @UserId OR (@HouseholdId IS NOT NULL AND a.HouseholdId = @HouseholdId))";
        using (var cmd = new SqlCommand(qAccounts, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)authUser.HouseholdId ?? DBNull.Value;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                accounts.Add(new {
                    id = reader.GetInt32(0),
                    name = reader.GetString(1),
                    balance = reader.GetDecimal(2),
                    currency = reader.GetString(3),
                    type = reader.GetString(4),
                    isShared = !reader.IsDBNull(5),
                    accountTypeId = reader.GetInt32(6)
                });
            }
        }

        // 1.7 Categorías
        var categories = new List<object>();
        var qCategories = @"
            SELECT CategoryId, Name, Type, ISNULL(Color, '#6BA292') 
            FROM BnkCategories 
            WHERE (HouseholdId IS NOT NULL AND HouseholdId = @HouseholdId)
               OR (UserId IS NOT NULL AND UserId = @UserId)
               OR (UserId IS NULL AND HouseholdId IS NULL)";
        using (var cmd = new SqlCommand(qCategories, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)authUser.HouseholdId ?? DBNull.Value;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                categories.Add(new { id = reader.GetInt32(0), name = reader.GetString(1), type = reader.GetString(2), color = reader.GetString(3) });
            }
        }

        // 1.8 Tipos de cuenta
        var accountTypes = new List<object>();
        using (var cmd = new SqlCommand("SELECT AccountTypeId, Name FROM BnkAccountTypes", conn))
        {
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                accountTypes.Add(new { id = reader.GetInt32(0), name = reader.GetString(1) });
            }
        }

        await response.WriteAsJsonAsync(new {
            user = new { authUser.UserId, authUser.FullName, authUser.Email },
            household = authUser.HouseholdId.HasValue ? new { id = authUser.HouseholdId, name = authUser.HouseholdName, role = authUser.Role,
                inviteCode = authUser.InviteCode } : null,
            summary = new { liquidBalance, creditCardDebt, monthlyIncome, monthlyExpense, householdExpenseThisMonth, totalAssetsValue },
            accounts,
            categories,
            accountTypes
        });
        return response;
    }

    // =========================================================================
    // 2. REGISTRAR TRANSACCIÓN CON ÍTEMS Y UBICACIÓN GPS (ATÓMICO)
    // =========================================================================
    [Function("CreateTransaction")]
    public async Task<HttpResponseData> CreateTransaction([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "finance/transactions")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var txDto = JsonSerializer.Deserialize<TransactionCreateDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (txDto == null || txDto.Amount <= 0) return req.CreateResponse(HttpStatusCode.BadRequest);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        using var sqlTx = conn.BeginTransaction();

        try
        {
            int? targetHouseholdId = (txDto.IsHouseholdExpense && authUser.HouseholdId.HasValue) ? authUser.HouseholdId.Value : null;

            var insertTxSql = @"
                INSERT INTO BnkTransactions (UserId, AccountId, CategoryId, HouseholdId, Amount, TransactionType, TransactionDate, Description, StoreName, LocationAddress, Latitude, Longitude)
                OUTPUT INSERTED.TransactionId
                VALUES (@UserId, @AccountId, @CategoryId, @HouseholdId, @Amount, @TransactionType, @TransactionDate, @Description, @StoreName, @LocationAddress, @Latitude, @Longitude);";

            long transactionId;
            using (var cmd = new SqlCommand(insertTxSql, conn, sqlTx))
            {
                cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
                cmd.Parameters.Add("@AccountId", SqlDbType.Int).Value = txDto.AccountId;
                cmd.Parameters.Add("@CategoryId", SqlDbType.Int).Value = (object?)txDto.CategoryId ?? DBNull.Value;
                cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)targetHouseholdId ?? DBNull.Value;
                cmd.Parameters.Add("@Amount", SqlDbType.Decimal).Value = txDto.Amount;
                cmd.Parameters.Add("@TransactionType", SqlDbType.VarChar).Value = txDto.TransactionType;
                cmd.Parameters.Add("@TransactionDate", SqlDbType.Date).Value = txDto.TransactionDate;
                cmd.Parameters.Add("@Description", SqlDbType.NVarChar).Value = (object?)txDto.Description ?? DBNull.Value;
                cmd.Parameters.Add("@StoreName", SqlDbType.NVarChar).Value = (object?)txDto.StoreName ?? DBNull.Value;
                cmd.Parameters.Add("@LocationAddress", SqlDbType.NVarChar).Value = (object?)txDto.LocationAddress ?? DBNull.Value;
                cmd.Parameters.Add("@Latitude", SqlDbType.Decimal).Value = (object?)txDto.Latitude ?? DBNull.Value;
                cmd.Parameters.Add("@Longitude", SqlDbType.Decimal).Value = (object?)txDto.Longitude ?? DBNull.Value;

                transactionId = (long)await cmd.ExecuteScalarAsync();
            }

            // Inserción de ítems de boleta (si existen)
            if (txDto.Items != null && txDto.Items.Count > 0)
            {
                var insertItemSql = @"
                    INSERT INTO BnkTransactionItems (TransactionId, CategoryId, ProductName, Quantity, UnitPrice, TotalPrice, Notes)
                    VALUES (@TransactionId, @CategoryId, @ProductName, @Quantity, @UnitPrice, @TotalPrice, @Notes);";

                foreach (var item in txDto.Items)
                {
                    using var itemCmd = new SqlCommand(insertItemSql, conn, sqlTx);
                    itemCmd.Parameters.Add("@TransactionId", SqlDbType.BigInt).Value = transactionId;
                    itemCmd.Parameters.Add("@CategoryId", SqlDbType.Int).Value = (object?)item.CategoryId ?? (object?)txDto.CategoryId ?? DBNull.Value;
                    itemCmd.Parameters.Add("@ProductName", SqlDbType.NVarChar).Value = item.ProductName;
                    itemCmd.Parameters.Add("@Quantity", SqlDbType.Decimal).Value = item.Quantity;
                    itemCmd.Parameters.Add("@UnitPrice", SqlDbType.Decimal).Value = item.UnitPrice;
                    itemCmd.Parameters.Add("@TotalPrice", SqlDbType.Decimal).Value = item.TotalPrice;
                    itemCmd.Parameters.Add("@Notes", SqlDbType.NVarChar).Value = (object?)item.Notes ?? DBNull.Value;
                    await itemCmd.ExecuteNonQueryAsync();
                }
            }

            // Actualización del saldo de la cuenta
            var modifier = txDto.TransactionType == "INCOME" ? txDto.Amount : -txDto.Amount;
            var updateBalanceSql = "UPDATE BnkAccounts SET CurrentBalance = CurrentBalance + @Modifier WHERE AccountId = @AccountId";

            using (var balanceCmd = new SqlCommand(updateBalanceSql, conn, sqlTx))
            {
                balanceCmd.Parameters.Add("@Modifier", SqlDbType.Decimal).Value = modifier;
                balanceCmd.Parameters.Add("@AccountId", SqlDbType.Int).Value = txDto.AccountId;
                await balanceCmd.ExecuteNonQueryAsync();
            }

            sqlTx.Commit();
            return req.CreateResponse(HttpStatusCode.Created);
        }
        catch (Exception ex)
        {
            sqlTx.Rollback();
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync(ex.Message);
            return err;
        }
    }

    // =========================================================================
    // 3. MÓDULO DE PATRIMONIO / INVENTARIO (BnkAssets)
    // =========================================================================
    [Function("GetAssets")]
    public async Task<HttpResponseData> GetAssets([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "finance/assets")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT AssetId, Name, Category, PurchasePrice, CurrentEstimatedValue, PurchaseDate, WarrantyExpirationDate, SerialNumber, RoomLocation, Notes, HouseholdId
            FROM BnkAssets
            WHERE UserId = @UserId OR (@HouseholdId IS NOT NULL AND HouseholdId = @HouseholdId)
            ORDER BY CurrentEstimatedValue DESC";

        var list = new List<object>();
        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)authUser.HouseholdId ?? DBNull.Value;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new {
                    id = reader.GetInt32(0),
                    name = reader.GetString(1),
                    category = reader.GetString(2),
                    purchasePrice = reader.GetDecimal(3),
                    currentEstimatedValue = reader.GetDecimal(4),
                    purchaseDate = reader.IsDBNull(5) ? null : reader.GetDateTime(5).ToString("yyyy-MM-dd"),
                    warrantyExpirationDate = reader.IsDBNull(6) ? null : reader.GetDateTime(6).ToString("yyyy-MM-dd"),
                    serialNumber = reader.IsDBNull(7) ? null : reader.GetString(7),
                    roomLocation = reader.IsDBNull(8) ? "General" : reader.GetString(8),
                    notes = reader.IsDBNull(9) ? null : reader.GetString(9),
                    isShared = !reader.IsDBNull(10)
                });
            }
        }

        await response.WriteAsJsonAsync(list);
        return response;
    }

    [Function("CreateAsset")]
    public async Task<HttpResponseData> CreateAsset([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "finance/assets")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var dto = JsonSerializer.Deserialize<AssetDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (dto == null || string.IsNullOrWhiteSpace(dto.Name)) return req.CreateResponse(HttpStatusCode.BadRequest);

        int? targetHouseholdId = (dto.IsSharedHousehold && authUser.HouseholdId.HasValue) ? authUser.HouseholdId.Value : null;

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            INSERT INTO BnkAssets (UserId, HouseholdId, Name, Category, PurchasePrice, CurrentEstimatedValue, PurchaseDate, WarrantyExpirationDate, SerialNumber, RoomLocation, Notes)
            VALUES (@UserId, @HouseholdId, @Name, @Category, @PurchasePrice, @CurrentEstimatedValue, @PurchaseDate, @WarrantyExpirationDate, @SerialNumber, @RoomLocation, @Notes);";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)targetHouseholdId ?? DBNull.Value;
            cmd.Parameters.Add("@Name", SqlDbType.NVarChar).Value = dto.Name;
            cmd.Parameters.Add("@Category", SqlDbType.VarChar).Value = dto.Category;
            cmd.Parameters.Add("@PurchasePrice", SqlDbType.Decimal).Value = dto.PurchasePrice;
            cmd.Parameters.Add("@CurrentEstimatedValue", SqlDbType.Decimal).Value = dto.CurrentEstimatedValue;
            cmd.Parameters.Add("@PurchaseDate", SqlDbType.Date).Value = (object?)dto.PurchaseDate ?? DBNull.Value;
            cmd.Parameters.Add("@WarrantyExpirationDate", SqlDbType.Date).Value = (object?)dto.WarrantyExpirationDate ?? DBNull.Value;
            cmd.Parameters.Add("@SerialNumber", SqlDbType.NVarChar).Value = (object?)dto.SerialNumber ?? DBNull.Value;
            cmd.Parameters.Add("@RoomLocation", SqlDbType.NVarChar).Value = (object?)dto.RoomLocation ?? DBNull.Value;
            cmd.Parameters.Add("@Notes", SqlDbType.NVarChar).Value = (object?)dto.Notes ?? DBNull.Value;

            await cmd.ExecuteNonQueryAsync();
        }

        return req.CreateResponse(HttpStatusCode.Created);
    }

    // =========================================================================
    // 4. SUSCRIPCIONES Y PAGOS FIJOS (BnkSubscriptions)
    // =========================================================================
    [Function("GetSubscriptions")]
    public async Task<HttpResponseData> GetSubscriptions([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "finance/subscriptions")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT s.SubscriptionId, s.Name, s.Amount, s.Currency, s.DueDay, s.AccountId, a.Name AS AccountName, s.HouseholdId
            FROM BnkSubscriptions s
            INNER JOIN BnkAccounts a ON s.AccountId = a.AccountId
            WHERE s.IsActive = 1 AND (s.UserId = @UserId OR (@HouseholdId IS NOT NULL AND s.HouseholdId = @HouseholdId))
            ORDER BY s.DueDay ASC";

        var list = new List<object>();
        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)authUser.HouseholdId ?? DBNull.Value;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new {
                    id = reader.GetInt32(0),
                    name = reader.GetString(1),
                    amount = reader.GetDecimal(2),
                    currency = reader.GetString(3),
                    dueDay = reader.GetInt32(4),
                    accountId = reader.GetInt32(5),
                    accountName = reader.GetString(6),
                    isShared = !reader.IsDBNull(7)
                });
            }
        }

        await response.WriteAsJsonAsync(list);
        return response;
    }

    [Function("CreateSubscription")]
    public async Task<HttpResponseData> CreateSubscription([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "finance/subscriptions")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var dto = JsonSerializer.Deserialize<SubscriptionDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (dto == null || string.IsNullOrWhiteSpace(dto.Name) || dto.Amount <= 0) return req.CreateResponse(HttpStatusCode.BadRequest);

        int? targetHouseholdId = (dto.IsSharedHousehold && authUser.HouseholdId.HasValue) ? authUser.HouseholdId.Value : null;

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            INSERT INTO BnkSubscriptions (UserId, HouseholdId, AccountId, CategoryId, Name, Amount, Currency, DueDay)
            VALUES (@UserId, @HouseholdId, @AccountId, @CategoryId, @Name, @Amount, @Currency, @DueDay);";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)targetHouseholdId ?? DBNull.Value;
            cmd.Parameters.Add("@AccountId", SqlDbType.Int).Value = dto.AccountId;
            cmd.Parameters.Add("@CategoryId", SqlDbType.Int).Value = (object?)dto.CategoryId ?? DBNull.Value;
            cmd.Parameters.Add("@Name", SqlDbType.NVarChar).Value = dto.Name;
            cmd.Parameters.Add("@Amount", SqlDbType.Decimal).Value = dto.Amount;
            cmd.Parameters.Add("@Currency", SqlDbType.VarChar).Value = string.IsNullOrEmpty(dto.Currency) ? "PEN" : dto.Currency;
            cmd.Parameters.Add("@DueDay", SqlDbType.Int).Value = dto.DueDay;

            await cmd.ExecuteNonQueryAsync();
        }

        return req.CreateResponse(HttpStatusCode.Created);
    }

    // =========================================================================
    // 5. METAS DE AHORRO (BnkSavingsGoals)
    // =========================================================================
    [Function("GetSavingsGoals")]
    public async Task<HttpResponseData> GetSavingsGoals([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "finance/goals")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            SELECT GoalId, Name, TargetAmount, CurrentSavedAmount, Currency, TargetDate, HouseholdId
            FROM BnkSavingsGoals
            WHERE UserId = @UserId OR (@HouseholdId IS NOT NULL AND HouseholdId = @HouseholdId)
            ORDER BY TargetDate ASC";

        var list = new List<object>();
        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)authUser.HouseholdId ?? DBNull.Value;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new {
                    id = reader.GetInt32(0),
                    name = reader.GetString(1),
                    targetAmount = reader.GetDecimal(2),
                    currentSavedAmount = reader.GetDecimal(3),
                    currency = reader.GetString(4),
                    targetDate = reader.IsDBNull(5) ? null : reader.GetDateTime(5).ToString("yyyy-MM-dd"),
                    isShared = !reader.IsDBNull(6)
                });
            }
        }

        await response.WriteAsJsonAsync(list);
        return response;
    }

    [Function("CreateSavingsGoal")]
    public async Task<HttpResponseData> CreateSavingsGoal([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "finance/goals")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var dto = JsonSerializer.Deserialize<SavingsGoalDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (dto == null || string.IsNullOrWhiteSpace(dto.Name) || dto.TargetAmount <= 0) return req.CreateResponse(HttpStatusCode.BadRequest);

        int? targetHouseholdId = (dto.IsSharedHousehold && authUser.HouseholdId.HasValue) ? authUser.HouseholdId.Value : null;

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            INSERT INTO BnkSavingsGoals (UserId, HouseholdId, Name, TargetAmount, CurrentSavedAmount, Currency, TargetDate)
            VALUES (@UserId, @HouseholdId, @Name, @TargetAmount, @CurrentSavedAmount, @Currency, @TargetDate);";

        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)targetHouseholdId ?? DBNull.Value;
            cmd.Parameters.Add("@Name", SqlDbType.NVarChar).Value = dto.Name;
            cmd.Parameters.Add("@TargetAmount", SqlDbType.Decimal).Value = dto.TargetAmount;
            cmd.Parameters.Add("@CurrentSavedAmount", SqlDbType.Decimal).Value = dto.CurrentSavedAmount;
            cmd.Parameters.Add("@Currency", SqlDbType.VarChar).Value = string.IsNullOrEmpty(dto.Currency) ? "PEN" : dto.Currency;
            cmd.Parameters.Add("@TargetDate", SqlDbType.Date).Value = (object?)dto.TargetDate ?? DBNull.Value;

            await cmd.ExecuteNonQueryAsync();
        }

        return req.CreateResponse(HttpStatusCode.Created);
    }

    // =========================================================================
    // 6. COMPENSACIÓN DEL HOGAR: ¿QUIÉN LE DEBE A QUIÉN? (Splitwise Style)
    // =========================================================================
    [Function("GetHouseholdSettlement")]
    public async Task<HttpResponseData> GetHouseholdSettlement([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "finance/household/settlement")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        // Obtener total gastado en la casa por cada miembro este mes
        var sql = @"
            SELECT u.UserId, u.FullName, ISNULL(SUM(t.Amount), 0) AS TotalPaid
            FROM BnkHouseholdMembers hm
            INNER JOIN BnkUsers u ON hm.UserId = u.UserId
            LEFT JOIN BnkTransactions t ON u.UserId = t.UserId 
                AND t.HouseholdId = hm.HouseholdId 
                AND t.TransactionType = 'EXPENSE'
                AND MONTH(t.TransactionDate) = MONTH(GETUTCDATE())
                AND YEAR(t.TransactionDate) = YEAR(GETUTCDATE())
            WHERE hm.HouseholdId = @HouseholdId
            GROUP BY u.UserId, u.FullName";

        var members = new List<(int UserId, string Name, decimal Paid)>();
        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = authUser.HouseholdId.Value;
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                members.Add((reader.GetInt32(0), reader.GetString(1), reader.GetDecimal(2)));
            }
        }

        decimal totalHouseholdExpense = members.Sum(m => m.Paid);
        decimal fairShare = members.Count > 0 ? totalHouseholdExpense / members.Count : 0;

        var breakdown = members.Select(m => new {
            userId = m.UserId,
            name = m.Name,
            paid = m.Paid,
            balance = m.Paid - fairShare // Positivo = Le deben dinero | Negativo = Debe dinero
        }).ToList();

        await response.WriteAsJsonAsync(new {
            totalHouseholdExpense,
            fairSharePerPerson = fairShare,
            memberCount = members.Count,
            breakdown
        });
        return response;
    }

    // =========================================================================
    // 7. CRUD DE CUENTAS & CATEGORÍAS
    // =========================================================================
    [Function("CreateAccount")]
    public async Task<HttpResponseData> CreateAccount([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "finance/accounts")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var dto = JsonSerializer.Deserialize<AccountCreateDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name)) return req.CreateResponse(HttpStatusCode.BadRequest);

        int? householdId = (dto.IsSharedHousehold && authUser.HouseholdId.HasValue) ? authUser.HouseholdId.Value : null;

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            INSERT INTO BnkAccounts (UserId, HouseholdId, AccountTypeId, Name, InitialBalance, CurrentBalance, Currency)
            VALUES (@UserId, @HouseholdId, @TypeId, @Name, @Initial, @Initial, @Currency)";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
        cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)householdId ?? DBNull.Value;
        cmd.Parameters.Add("@TypeId", SqlDbType.Int).Value = dto.AccountTypeId;
        cmd.Parameters.Add("@Name", SqlDbType.NVarChar).Value = dto.Name;
        cmd.Parameters.Add("@Initial", SqlDbType.Decimal).Value = dto.InitialBalance;
        cmd.Parameters.Add("@Currency", SqlDbType.VarChar).Value = string.IsNullOrEmpty(dto.Currency) ? "PEN" : dto.Currency;

        await cmd.ExecuteNonQueryAsync();
        return req.CreateResponse(HttpStatusCode.Created);
    }

    [Function("UpdateAccount")]
    public async Task<HttpResponseData> UpdateAccount([HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "finance/accounts")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var dto = JsonSerializer.Deserialize<AccountUpdateDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name)) return req.CreateResponse(HttpStatusCode.BadRequest);

        int? householdId = (dto.IsSharedHousehold && authUser.HouseholdId.HasValue) ? authUser.HouseholdId.Value : null;

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            UPDATE BnkAccounts 
            SET Name = @Name, AccountTypeId = @TypeId, Currency = @Currency, HouseholdId = @HouseholdId
            WHERE AccountId = @AccountId AND (UserId = @UserId OR (@UserHouseholdId IS NOT NULL AND HouseholdId = @UserHouseholdId))";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@Name", SqlDbType.NVarChar).Value = dto.Name;
        cmd.Parameters.Add("@TypeId", SqlDbType.Int).Value = dto.AccountTypeId;
        cmd.Parameters.Add("@Currency", SqlDbType.VarChar).Value = string.IsNullOrEmpty(dto.Currency) ? "PEN" : dto.Currency;
        cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)householdId ?? DBNull.Value;
        cmd.Parameters.Add("@AccountId", SqlDbType.Int).Value = dto.AccountId;
        cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
        cmd.Parameters.Add("@UserHouseholdId", SqlDbType.Int).Value = (object?)authUser.HouseholdId ?? DBNull.Value;

        int affected = await cmd.ExecuteNonQueryAsync();
        return affected > 0 ? req.CreateResponse(HttpStatusCode.OK) : req.CreateResponse(HttpStatusCode.NotFound);
    }

    [Function("DeleteAccount")]
    public async Task<HttpResponseData> DeleteAccount([HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "finance/accounts/{id:int}")] HttpRequestData req, int id)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = @"
            UPDATE BnkAccounts 
            SET IsActive = 0 
            WHERE AccountId = @AccountId AND (UserId = @UserId OR (@UserHouseholdId IS NOT NULL AND HouseholdId = @UserHouseholdId))";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@AccountId", SqlDbType.Int).Value = id;
        cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
        cmd.Parameters.Add("@UserHouseholdId", SqlDbType.Int).Value = (object?)authUser.HouseholdId ?? DBNull.Value;

        int affected = await cmd.ExecuteNonQueryAsync();
        return affected > 0 ? req.CreateResponse(HttpStatusCode.OK) : req.CreateResponse(HttpStatusCode.NotFound);
    }

// 2. LISTAR TRANSACCIONES CON PAGINACIÓN Y FILTROS AVANZADOS (GET /api/finance/transactions)
    [Function("GetTransactions")]
    public async Task<HttpResponseData> GetTransactions([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "finance/transactions")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }


        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        // Parámetros de Paginación
        int page = int.TryParse(query["page"], out var p) ? Math.Max(1, p) : 1;
        int pageSize = int.TryParse(query["pageSize"], out var ps) ? Math.Clamp(ps, 5, 50) : 20;
        int offset = (page - 1) * pageSize;

        // Parámetros de Filtro
        string scope = (query["scope"] ?? "ALL").ToUpper();
        string? filterType = query["type"];
        int? accountId = int.TryParse(query["accountId"], out var accId) ? accId : null;
        int? categoryId = int.TryParse(query["categoryId"], out var catId) ? catId : null;
        string? search = query["search"]?.Trim();

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        // 1. Construir la cláusula WHERE dinámica
        var whereConditions = new List<string>();

        if (scope == "HOUSEHOLD" && authUser.HouseholdId.HasValue)
            whereConditions.Add("t.HouseholdId = @HouseholdId");
        else if (scope == "PERSONAL")
            whereConditions.Add("t.UserId = @UserId AND t.HouseholdId IS NULL");
        else
            whereConditions.Add("(t.UserId = @UserId OR (@HouseholdId IS NOT NULL AND t.HouseholdId = @HouseholdId))");

        if (!string.IsNullOrEmpty(filterType) && (filterType == "INCOME" || filterType == "EXPENSE"))
            whereConditions.Add("t.TransactionType = @FilterType");

        if (accountId.HasValue && accountId.Value > 0)
            whereConditions.Add("t.AccountId = @AccountId");

        if (categoryId.HasValue && categoryId.Value > 0)
            whereConditions.Add("t.CategoryId = @CategoryId");

        if (!string.IsNullOrEmpty(search))
            whereConditions.Add("(t.Description LIKE @Search OR t.StoreName LIKE @Search OR t.LocationAddress LIKE @Search)");

        string whereClause = string.Join(" AND ", whereConditions);

        // 2. Obtener el TOTAL de registros para saber si hay más páginas
        var countSql = $"SELECT COUNT(1) FROM BnkTransactions t WHERE {whereClause}";
        int totalItems = 0;
        using (var countCmd = new SqlCommand(countSql, conn))
        {
            countCmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            countCmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)authUser.HouseholdId ?? DBNull.Value;
            if (!string.IsNullOrEmpty(filterType)) countCmd.Parameters.Add("@FilterType", SqlDbType.VarChar).Value = filterType;
            if (accountId.HasValue && accountId.Value > 0) countCmd.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId.Value;
            if (categoryId.HasValue && categoryId.Value > 0) countCmd.Parameters.Add("@CategoryId", SqlDbType.Int).Value = categoryId.Value;
            if (!string.IsNullOrEmpty(search)) countCmd.Parameters.Add("@Search", SqlDbType.NVarChar).Value = $"%{search}%";

            totalItems = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
        }

        // 3. Consulta Paginada con OFFSET / FETCH NEXT (Ultra rápida)
        var sql = $@"
            SELECT t.TransactionId, t.Amount, t.TransactionType, t.TransactionDate, 
                   ISNULL(t.Description, '') AS Description, a.Name AS AccountName, 
                   ISNULL(c.Name, 'General') AS CategoryName, a.Currency,
                   u.FullName AS PaidBy, (CASE WHEN t.HouseholdId IS NOT NULL THEN 1 ELSE 0 END) AS IsHousehold,
                   t.StoreName, t.LocationAddress
            FROM BnkTransactions t
            INNER JOIN BnkAccounts a ON t.AccountId = a.AccountId
            INNER JOIN BnkUsers u ON t.UserId = u.UserId
            LEFT JOIN BnkCategories c ON t.CategoryId = c.CategoryId
            WHERE {whereClause}
            ORDER BY t.TransactionDate DESC, t.TransactionId DESC
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        var list = new List<object>();
        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
            cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)authUser.HouseholdId ?? DBNull.Value;
            cmd.Parameters.Add("@Offset", SqlDbType.Int).Value = offset;
            cmd.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;
            if (!string.IsNullOrEmpty(filterType)) cmd.Parameters.Add("@FilterType", SqlDbType.VarChar).Value = filterType;
            if (accountId.HasValue && accountId.Value > 0) cmd.Parameters.Add("@AccountId", SqlDbType.Int).Value = accountId.Value;
            if (categoryId.HasValue && categoryId.Value > 0) cmd.Parameters.Add("@CategoryId", SqlDbType.Int).Value = categoryId.Value;
            if (!string.IsNullOrEmpty(search)) cmd.Parameters.Add("@Search", SqlDbType.NVarChar).Value = $"%{search}%";

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new {
                    id = reader.GetInt64(0),
                    amount = reader.GetDecimal(1),
                    type = reader.GetString(2),
                    date = reader.GetDateTime(3).ToString("yyyy-MM-dd"),
                    description = reader.GetString(4),
                    account = reader.GetString(5),
                    category = reader.GetString(6),
                    currency = reader.GetString(7),
                    paidBy = reader.GetString(8),
                    isHousehold = reader.GetInt32(9) == 1,
                    storeName = reader.IsDBNull(10) ? null : reader.GetString(10),
                    locationAddress = reader.IsDBNull(11) ? null : reader.GetString(11)
                });
            }
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new {
            items = list,
            page,
            pageSize,
            totalItems,
            totalPages = (int)Math.Ceiling((double)totalItems / pageSize),
            hasMore = (offset + list.Count) < totalItems
        });
        return response;
    }

[Function("CreateCategory")]
    public async Task<HttpResponseData> CreateCategory([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "finance/categories")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null)
        {
            var unauth = req.CreateResponse(HttpStatusCode.Unauthorized);
            await unauth.WriteStringAsync($"401 Unauthorized: {errorMsg}");
            return unauth;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var dto = JsonSerializer.Deserialize<CategoryCreateDto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name)) return req.CreateResponse(HttpStatusCode.BadRequest);

        // Validar que sea uno de los 3 tipos permitidos
        var validTypes = new[] { "INCOME", "EXPENSE", "ASSET" };
        var type = dto.Type.ToUpper();
        if (!validTypes.Contains(type)) return req.CreateResponse(HttpStatusCode.BadRequest);

        using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        var sql = "INSERT INTO BnkCategories (HouseholdId, UserId, Name, Type, Color) VALUES (@HouseholdId, @UserId, @Name, @Type, @Color)";
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@HouseholdId", SqlDbType.Int).Value = (object?)authUser.HouseholdId ?? DBNull.Value;
        cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = authUser.UserId;
        cmd.Parameters.Add("@Name", SqlDbType.NVarChar).Value = dto.Name;
        cmd.Parameters.Add("@Type", SqlDbType.VarChar).Value = type;
        cmd.Parameters.Add("@Color", SqlDbType.NVarChar).Value = (object?)dto.Color ?? "#6BA292";

        await cmd.ExecuteNonQueryAsync();
        return req.CreateResponse(HttpStatusCode.Created);
    }

    // =========================================================================
    // ESCANEAR BOLETA MULTI-FOTO CON AZURE AI (FUSIÓN INTELIGENTE)
    // =========================================================================
    [Function("ScanReceipt")]
    public async Task<HttpResponseData> ScanReceipt(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "finance/scan-receipt")] HttpRequestData req)
    {
        var (authUser, errorMsg) = await AuthHelper.ValidateTokenAsync(req);
        if (authUser == null) return req.CreateResponse(HttpStatusCode.Unauthorized);

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var dto = JsonSerializer.Deserialize<ScanReceiptRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (dto == null || dto.ImagesBase64 == null || dto.ImagesBase64.Count == 0)
            return req.CreateResponse(HttpStatusCode.BadRequest);

        try
        {
            // 1. Preparar bytes de cada imagen
            var imagesBytesList = new List<byte[]>();
            foreach (var b64 in dto.ImagesBase64)
            {
                if (string.IsNullOrWhiteSpace(b64)) continue;
                string cleanBase64 = b64.Contains(",") ? b64.Split(',')[1] : b64;
                imagesBytesList.Add(Convert.FromBase64String(cleanBase64));
            }

            if (imagesBytesList.Count == 0)
                return req.CreateResponse(HttpStatusCode.BadRequest);

            // 2. Procesar TODAS las fotos en paralelo en Azure para máxima velocidad
            var tasks = imagesBytesList.Select(img => AiScanHelper.ScanReceiptAsync(img));
            var results = await Task.WhenAll(tasks);

            // 3. Fusión de Resultados (Merge)
            string storeName = "Comercio";
            string date = DateTime.UtcNow.ToString("yyyy-MM-dd");
            decimal maxDetectedTotal = 0.0m;
            var allItems = new List<TransactionItemDto>();

            foreach (var res in results)
            {
                if (res.StoreName != null && res.StoreName != "Comercio")
                    storeName = res.StoreName;

                if (!string.IsNullOrEmpty(res.TransactionDate))
                    date = res.TransactionDate;

                if (res.Amount > maxDetectedTotal)
                    maxDetectedTotal = res.Amount;

                if (res.Items != null && res.Items.Count > 0)
                    allItems.AddRange(res.Items);
            }

            // Si el total detectado en pie de página es menor a la suma de todos los ítems fusionados, usar la suma
            decimal sumOfItems = allItems.Sum(x => x.TotalPrice);
            decimal finalAmount = maxDetectedTotal > 0 ? maxDetectedTotal : sumOfItems;

            var finalResult = new ScanResultDto(
                TransactionType: "EXPENSE",
                Amount: finalAmount,
                TransactionDate: date,
                StoreName: storeName,
                Description: $"Compra en {storeName} ({dto.ImagesBase64.Count} fotos)",
                Items: allItems.Count > 0 ? allItems : null
            );

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(finalResult);
            return response;
        }
        catch (Exception ex)
        {
            var errResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errResponse.WriteStringAsync($"Error procesando fotos: {ex.Message}");
            return errResponse;
        }
    }
}