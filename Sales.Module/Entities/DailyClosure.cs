using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

public class DailyClosure
{
    public int Id { get; set; }

    public DateTime ClosureDate { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? UserId { get; set; } = "Admin";

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalExpectedBsS { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalActualBsS { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalDifferenceBsS { get; set; }

    [MaxLength(500)]
    public string? Observation { get; set; }

    public List<ClosureDetail> Details { get; set; } = new();
}
