using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.DTOs;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{
    public async Task<SaleDto> UpdateSaleCustomerAsync(int saleId, int customerId, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var sale = await GetSaleEntityAsync(saleId);
        EnsureHoldClaimAccess(sale, actingUserId);

        if (sale.Status == SaleStatus.Completed || sale.Status == SaleStatus.Cancelled)
            throw new InvalidOperationException("No se puede modificar el cliente de una venta finalizada.");

        if (sale.Status == SaleStatus.OnHold && sale.Payments.Any())
            throw new InvalidOperationException("Una cuenta abierta con pagos registrados no permite cambio de titular.");

        var customer = await _context.Customers.FindAsync(new object[] { customerId }, cancellationToken);
        if (customer == null) throw new KeyNotFoundException($"Cliente con ID {customerId} no encontrado.");
        if (!customer.IsActive) throw new InvalidOperationException("No se puede asignar un cliente inactivo a la venta.");

        sale.CustomerId = customer.Id;
        sale.CustomerName = customer.Name;
        sale.CustomerCedula = customer.CedulaOrRif;

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(sale);
    }

    // 8.206: el catálogo de clientes vive en SalesCustomerService; la fachada conserva el
    // contrato histórico de ISalesService para no romper controladores ni tests.
    public Task<CustomerDto> GetDefaultCustomerAsync(System.Threading.CancellationToken cancellationToken = default)
        => _customers.GetDefaultCustomerAsync(cancellationToken);

    public Task<(IEnumerable<CustomerDto> Items, int TotalCount)> GetCustomersAsync(
        string? query = null,
        int page = 1,
        int pageSize = 20,
        bool recentOnly = false,
        System.Threading.CancellationToken cancellationToken = default)
        => _customers.GetCustomersAsync(query, page, pageSize, recentOnly, cancellationToken);

    public Task<CustomerDto> CreateCustomerAsync(CreateCustomerDto request, System.Threading.CancellationToken cancellationToken = default)
        => _customers.CreateCustomerAsync(request, cancellationToken);

    public Task<CustomerDto> UpdateCustomerAsync(int id, UpdateCustomerDto request, System.Threading.CancellationToken cancellationToken = default)
        => _customers.UpdateCustomerAsync(id, request, cancellationToken);

    public Task DeleteCustomerAsync(int id, System.Threading.CancellationToken cancellationToken = default)
        => _customers.DeleteCustomerAsync(id, cancellationToken);
}
