using System.Collections.Generic;

namespace Core.DTOs;

public class CustomerDto
{
    public int Id { get; set; }
    public string CedulaOrRif { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public decimal CreditLimitUSD { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDefault { get; set; }
}

public class CreateCustomerDto
{
    public string CedulaOrRif { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public decimal CreditLimitUSD { get; set; } = 0m;
}

public class UpdateCustomerDto
{
    public string CedulaOrRif { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public decimal CreditLimitUSD { get; set; } = 0m;
    public bool IsActive { get; set; } = true;
}

public class CustomerPagedResultDto
{
    public List<CustomerDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
