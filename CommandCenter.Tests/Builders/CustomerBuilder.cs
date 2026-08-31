using System;
using Core.Entities;

namespace CommandCenter.Tests.Builders;

public class CustomerBuilder
{
    private int _id = 1;
    private string _cedulaOrRif = "V-12345678";
    private string _name = "Cliente Prueba";
    private string _phone = "0414-1234567";
    private decimal _creditLimitUsd = 500m;
    private bool _isActive = true;
    private bool _isDefault = false;

    public CustomerBuilder WithId(int id) { _id = id; return this; }
    public CustomerBuilder WithCedula(string cedula) { _cedulaOrRif = cedula; return this; }
    public CustomerBuilder WithName(string name) { _name = name; return this; }
    public CustomerBuilder WithPhone(string phone) { _phone = phone; return this; }
    public CustomerBuilder WithCreditLimit(decimal limit) { _creditLimitUsd = limit; return this; }
    public CustomerBuilder AsDefault()
    {
        _isDefault = true;
        _cedulaOrRif = "V-00000000";
        _name = "Consumidor Final";
        return this;
    }
    public CustomerBuilder AsInactive() { _isActive = false; return this; }

    public Customer Build() => new Customer
    {
        Id = _id,
        CedulaOrRif = _cedulaOrRif,
        Name = _name,
        Phone = _phone,
        CreditLimitUSD = _creditLimitUsd,
        IsActive = _isActive,
        IsDefault = _isDefault
    };
}
