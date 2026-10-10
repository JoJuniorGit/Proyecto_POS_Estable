using System;
using System.Linq;
using Core.Entities;
using Inventory.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.154-T3 (SRE-01): deterministic lock acquisition order for supplier invoice applies.
/// </summary>
public class SupplierInvoiceApplyOrderingTests
{
    private static SupplierInvoiceLine Line(int? resolvedProductId, int id) => new()
    {
        Id = id,
        ResolvedProductId = resolvedProductId
    };

    [Fact]
    public void OrderForApply_UnsortedProducts_ReturnsAscendingByResolvedProductId()
    {
        var lines = new[] { Line(7, 1), Line(3, 2), Line(5, 3) };

        var ordered = SupplierInvoiceApplyOrdering.OrderForApply(lines);

        Assert.Equal(new int?[] { 3, 5, 7 }, ordered.Select(line => line.ResolvedProductId));
    }

    [Fact]
    public void OrderForApply_NullsAndTies_ReturnsNullsFirstThenTiesById()
    {
        var lines = new[] { Line(null, 1), Line(5, 9), Line(5, 4) };

        var ordered = SupplierInvoiceApplyOrdering.OrderForApply(lines);

        Assert.Equal(new int?[] { null, 5, 5 }, ordered.Select(line => line.ResolvedProductId));
        Assert.Equal(new[] { 1, 4, 9 }, ordered.Select(line => line.Id));
    }

    [Fact]
    public void OrderForApply_ReversedInput_ReturnsIdenticalOrder()
    {
        var forward = new[] { Line(null, 2), Line(4, 5), Line(4, 3), Line(9, 7) };

        var orderedForward = SupplierInvoiceApplyOrdering.OrderForApply(forward);
        var orderedReversed = SupplierInvoiceApplyOrdering.OrderForApply(forward.Reverse().ToArray());

        Assert.Equal(orderedForward.Select(line => line.Id), orderedReversed.Select(line => line.Id));
        Assert.Equal(orderedForward.Select(line => line.ResolvedProductId), orderedReversed.Select(line => line.ResolvedProductId));
    }

    [Fact]
    public void OrderForApply_EmptyInput_ReturnsEmpty()
    {
        var ordered = SupplierInvoiceApplyOrdering.OrderForApply(Array.Empty<SupplierInvoiceLine>());

        Assert.Empty(ordered);
    }

    [Fact]
    public void OrderForApply_SingleLine_ReturnsSameLine()
    {
        var line = Line(42, 8);

        var ordered = SupplierInvoiceApplyOrdering.OrderForApply(new[] { line });

        Assert.Same(line, Assert.Single(ordered));
    }
}
