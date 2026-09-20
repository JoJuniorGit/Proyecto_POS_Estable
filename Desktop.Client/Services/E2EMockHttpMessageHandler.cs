using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.Services;

public class E2EMockHttpMessageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.PathAndQuery ?? string.Empty;
        string responseJson = "{}";
        var statusCode = HttpStatusCode.OK;

        if (uri.Contains("api/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            responseJson = """
            {
                "token": "e2e-mock-jwt-token",
                "user": {
                    "id": 1,
                    "name": "Administrador E2E",
                    "role": 3,
                    "cedula": "admin",
                    "isActive": true
                },
                "requiresPasswordChange": false
            }
            """;
        }
        else if (uri.Contains("api/auth/me", StringComparison.OrdinalIgnoreCase))
        {
            responseJson = """
            {
                "id": 1,
                "name": "Administrador E2E",
                "role": 3,
                "cedula": "admin",
                "isActive": true
            }
            """;
        }
        else if (uri.Contains("api/products", StringComparison.OrdinalIgnoreCase))
        {
            responseJson = """
            [
                {
                    "id": 1,
                    "barcode": "7591001",
                    "name": "Harina PAN",
                    "price": 1.50,
                    "cost": 1.00,
                    "stock": 100,
                    "minStock": 10,
                    "isActive": true
                }
            ]
            """;
        }
        else if (uri.Contains("api/cash-drawer", StringComparison.OrdinalIgnoreCase))
        {
            responseJson = """
            {
                "isOpen": true,
                "currentCashUSD": 150.00,
                "currentCashVES": 7500.00,
                "startingCashUSD": 100.00,
                "startingCashVES": 5000.00
            }
            """;
        }
        else if (uri.Contains("api/exchange-rate", StringComparison.OrdinalIgnoreCase))
        {
            responseJson = """
            {
                "rate": 50.00,
                "source": "BCV",
                "lastUpdated": "2026-09-20T00:00:00Z"
            }
            """;
        }
        else if (uri.Contains("api/users", StringComparison.OrdinalIgnoreCase))
        {
            responseJson = """
            [
                {
                    "id": 1,
                    "name": "Administrador E2E",
                    "role": 3,
                    "cedula": "admin",
                    "isActive": true
                }
            ]
            """;
        }
        else if (uri.Contains("api/orders", StringComparison.OrdinalIgnoreCase) ||
                 uri.Contains("api/pickups", StringComparison.OrdinalIgnoreCase) ||
                 uri.Contains("api/sales", StringComparison.OrdinalIgnoreCase))
        {
            responseJson = "[]";
        }
        else if (uri.Contains("api/daily-closure", StringComparison.OrdinalIgnoreCase))
        {
            responseJson = """
            {
                "isClosed": false,
                "totalSalesUSD": 0.00,
                "totalSalesVES": 0.00
            }
            """;
        }
        else if (uri.Contains("api/settings", StringComparison.OrdinalIgnoreCase))
        {
            responseJson = """
            {
                "businessName": "CommandCenter POS",
                "receiptFooter": "Gracias por su compra"
            }
            """;
        }
        else if (uri.Contains("api/system/version", StringComparison.OrdinalIgnoreCase))
        {
            responseJson = """
            {
                "isClientCompatible": true,
                "minimumClientVersion": "0.1.0",
                "serverVersion": "0.1.0"
            }
            """;
        }

        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}
