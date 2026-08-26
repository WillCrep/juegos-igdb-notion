using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinanceApp.Models;

namespace FinanceApp;

public static class AiScanHelper
{
    private static readonly HttpClient HttpClient = new();

    private static string GetEndpoint() =>
        Environment.GetEnvironmentVariable("AZURE_DOC_INTELLIGENCE_ENDPOINT") ?? "";

    private static string GetKey() =>
        Environment.GetEnvironmentVariable("AZURE_DOC_INTELLIGENCE_KEY") ?? "";

    // =========================================================================
    // ESCÁNER DE BOLETAS Y FACTURAS CON EXTRACCIÓN MEJORADA
    // =========================================================================
    public static async Task<ScanResultDto> ScanReceiptAsync(byte[] imageBytes)
    {
        var endpoint = GetEndpoint().TrimEnd('/');
        var key = GetKey();

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Faltan las variables 'AZURE_DOC_INTELLIGENCE_ENDPOINT' o 'AZURE_DOC_INTELLIGENCE_KEY'.");

        var requestUri = $"{endpoint}/formrecognizer/documentModels/prebuilt-receipt:analyze?api-version=2023-07-31";

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);
        request.Content = new ByteArrayContent(imageBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");

        var response = await HttpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new Exception($"Error de Azure Document Intelligence: {response.StatusCode} - {err}");
        }

        var operationLocation = response.Headers.GetValues("Operation-Location").FirstOrDefault();
        if (string.IsNullOrEmpty(operationLocation))
            throw new Exception("No se recibió la cabecera Operation-Location de Azure.");

        // Polling para esperar resultado
        JsonDocument? resultDoc = null;
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(1200);
            using var pollReq = new HttpRequestMessage(HttpMethod.Get, operationLocation);
            pollReq.Headers.Add("Ocp-Apim-Subscription-Key", key);
            var pollRes = await HttpClient.SendAsync(pollReq);
            var json = await pollRes.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("status").GetString();

            if (status == "succeeded")
            {
                resultDoc = doc;
                break;
            }
            if (status == "failed")
                throw new Exception("El análisis del documento falló en Azure.");
        }

        if (resultDoc == null)
            throw new TimeoutException("Tiempo de espera agotado analizando la boleta.");

        // Parsear datos de la boleta
        string storeName = "Comercio";
        decimal totalAmount = 0.0m;
        string date = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var items = new List<TransactionItemDto>();

        var analyzeResult = resultDoc.RootElement.GetProperty("analyzeResult");
        if (analyzeResult.TryGetProperty("documents", out var documents) && documents.GetArrayLength() > 0)
        {
            var doc = documents[0];
            var fields = doc.GetProperty("fields");

            // 1. Nombre del Comercio
            if (fields.TryGetProperty("MerchantName", out var m))
                storeName = ExtractString(m) ?? storeName;

            // 2. Monto Total
            if (fields.TryGetProperty("Total", out var t))
                totalAmount = ExtractDecimal(t);

            // 3. Fecha
            if (fields.TryGetProperty("TransactionDate", out var d))
                date = ExtractDate(d) ?? date;

            // 4. Ítems / Productos con Extracción Profunda
            if (fields.TryGetProperty("Items", out var itArray) && itArray.TryGetProperty("valueArray", out var arr))
            {
                foreach (var it in arr.EnumerateArray())
                {
                    if (!it.TryGetProperty("valueObject", out var itFields)) continue;

                    string name = "Producto";
                    decimal qty = 1.0m;
                    decimal unitPrice = 0.0m;
                    decimal totalPrice = 0.0m;

                    // Nombre del Producto
                    if (itFields.TryGetProperty("Description", out var desc))
                        name = ExtractString(desc) ?? name;

                    // Cantidad
                    if (itFields.TryGetProperty("Quantity", out var q))
                        qty = ExtractDecimal(q);
                    if (qty <= 0) qty = 1.0m;

                    // Precio Total del Ítem
                    if (itFields.TryGetProperty("TotalPrice", out var tp))
                        totalPrice = ExtractDecimal(tp);

                    // Precio Unitario del Ítem
                    if (itFields.TryGetProperty("Price", out var p))
                        unitPrice = ExtractDecimal(p);

                    // Autocalcular precios si uno de ellos vino en 0
                    if (totalPrice > 0 && unitPrice == 0 && qty > 0)
                        unitPrice = Math.Round(totalPrice / qty, 2);
                    else if (unitPrice > 0 && totalPrice == 0 && qty > 0)
                        totalPrice = Math.Round(unitPrice * qty, 2);

                    // Si ambos siguen en 0, intentar extraer precios desde el texto de la descripción
                    if (totalPrice == 0 && unitPrice == 0)
                    {
                        var extracted = TryExtractPricesFromText(name);
                        if (extracted.HasValue)
                        {
                            name = extracted.Value.CleanName;
                            qty = extracted.Value.Quantity > 0 ? extracted.Value.Quantity : qty;
                            unitPrice = extracted.Value.UnitPrice;
                            totalPrice = extracted.Value.TotalPrice;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        items.Add(new TransactionItemDto(
                            ProductName: name.Trim(),
                            Quantity: qty,
                            UnitPrice: unitPrice,
                            TotalPrice: totalPrice,
                            CategoryId: null,
                            Notes: null
                        ));
                    }
                }
            }
        }

        // Si el total general vino en 0 pero hay ítems, sumar los subtotales
        if (totalAmount == 0 && items.Count > 0)
            totalAmount = items.Sum(x => x.TotalPrice);

        return new ScanResultDto(
            TransactionType: "EXPENSE",
            Amount: totalAmount,
            TransactionDate: date,
            StoreName: storeName,
            Description: $"Compra en {storeName}",
            Items: items.Count > 0 ? items : null
        );
    }

    // =========================================================================
    // HELPERS DE EXTRACCIÓN ROBUSTA (SOPORTA FORMATOS DE PERÚ / LATAM)
    // =========================================================================

    private static decimal ExtractDecimal(JsonElement element)
    {
        // 1. Intentar por valueCurrency (ej. { amount: 12.50, symbol: "$" })
        if (element.TryGetProperty("valueCurrency", out var cur))
        {
            if (cur.TryGetProperty("amount", out var amt))
                return (decimal)amt.GetDouble();
        }

        // 2. Intentar por valueNumber
        if (element.TryGetProperty("valueNumber", out var num))
            return (decimal)num.GetDouble();

        // 3. Intentar por valueInteger
        if (element.TryGetProperty("valueInteger", out var intNum))
            return (decimal)intNum.GetInt64();

        // 4. Intentar por texto crudo 'content' (ej: "S/ 14.50", "14,50", "S/. 25.00")
        if (element.TryGetProperty("content", out var contentProp))
        {
            var text = contentProp.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                // Limpiar símbolos de soles, comas y espacios
                var match = Regex.Match(text, @"\d+(?:[\.,]\d{1,2})?");
                if (match.Success)
                {
                    var cleanNumber = match.Value.Replace(',', '.');
                    if (decimal.TryParse(cleanNumber, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                        return parsed;
                }
            }
        }

        return 0.0m;
    }

    private static string? ExtractString(JsonElement element)
    {
        if (element.TryGetProperty("valueString", out var vStr))
            return vStr.GetString();
        if (element.TryGetProperty("content", out var cStr))
            return cStr.GetString();
        return null;
    }

    private static string? ExtractDate(JsonElement element)
    {
        if (element.TryGetProperty("valueDate", out var vDate))
            return vDate.GetString();
        if (element.TryGetProperty("content", out var cDate))
        {
            var text = cDate.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                // Intentar parsear formato DD/MM/YYYY o YYYY-MM-DD
                if (DateTime.TryParse(text, new CultureInfo("es-PE"), DateTimeStyles.None, out var dt))
                    return dt.ToString("yyyy-MM-dd");
            }
        }
        return null;
    }

    // Detector de precios embebidos en el nombre (típico de tickets de Tambo/Tottus)
    private static (string CleanName, decimal Quantity, decimal UnitPrice, decimal TotalPrice)? TryExtractPricesFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Patrón: "LECHE GLORIA 1L 3 X 4.50" o "ARROZ EXTRA 21.50"
        var matchQtyPrice = Regex.Match(text, @"^(.*?)(?:\s+(\d+(?:[.,]\d+)?)\s*(?:x|X|\*|UN)\s*(\d+(?:[.,]\d+)?))?\s+(\d+(?:[.,]\d{2}))$");
        if (matchQtyPrice.Success)
        {
            string cleanName = matchQtyPrice.Groups[1].Value;
            decimal qty = 1.0m;
            decimal unitPrice = 0.0m;
            decimal totalPrice = 0.0m;

            if (matchQtyPrice.Groups[2].Success && decimal.TryParse(matchQtyPrice.Groups[2].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var q))
                qty = q;

            if (matchQtyPrice.Groups[3].Success && decimal.TryParse(matchQtyPrice.Groups[3].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var up))
                unitPrice = up;

            if (matchQtyPrice.Groups[4].Success && decimal.TryParse(matchQtyPrice.Groups[4].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var tp))
                totalPrice = tp;

            if (totalPrice > 0 && unitPrice == 0 && qty > 0)
                unitPrice = Math.Round(totalPrice / qty, 2);

            return (cleanName, qty, unitPrice, totalPrice);
        }

        return null;
    }
}