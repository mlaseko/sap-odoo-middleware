using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class CogsHashTests
{
    [Fact]
    public void ComputeCogsHash_SamePayload_ReturnsSameHash()
    {
        var request = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines =
            [
                new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 80.0 },
                new CogsJournalLineRequest { LineNum = 1, ItemCode = "ITEM002", Quantity = 3, UnitCost = 40.0 }
            ]
        };

        var hash1 = OdooJsonRpcService.ComputeCogsHash(request);
        var hash2 = OdooJsonRpcService.ComputeCogsHash(request);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 hex = 64 chars
    }

    [Fact]
    public void ComputeCogsHash_DifferentDocEntry_ReturnsDifferentHash()
    {
        var request1 = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines = [new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 80.0 }]
        };

        var request2 = new CogsJournalRequest
        {
            DocEntry = 701,
            Lines = [new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 80.0 }]
        };

        Assert.NotEqual(
            OdooJsonRpcService.ComputeCogsHash(request1),
            OdooJsonRpcService.ComputeCogsHash(request2));
    }

    [Fact]
    public void ComputeCogsHash_DifferentQuantity_ReturnsDifferentHash()
    {
        var request1 = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines = [new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 80.0 }]
        };

        var request2 = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines = [new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 10, UnitCost = 80.0 }]
        };

        Assert.NotEqual(
            OdooJsonRpcService.ComputeCogsHash(request1),
            OdooJsonRpcService.ComputeCogsHash(request2));
    }

    [Fact]
    public void ComputeCogsHash_DifferentCost_ReturnsDifferentHash()
    {
        var request1 = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines = [new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 80.0 }]
        };

        var request2 = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines = [new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 100.0 }]
        };

        Assert.NotEqual(
            OdooJsonRpcService.ComputeCogsHash(request1),
            OdooJsonRpcService.ComputeCogsHash(request2));
    }

    [Fact]
    public void ComputeCogsHash_LinesReordered_ReturnsSameHash()
    {
        // Lines should be sorted by LineNum, so order in the list shouldn't matter
        var request1 = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines =
            [
                new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 80.0 },
                new CogsJournalLineRequest { LineNum = 1, ItemCode = "ITEM002", Quantity = 3, UnitCost = 40.0 }
            ]
        };

        var request2 = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines =
            [
                new CogsJournalLineRequest { LineNum = 1, ItemCode = "ITEM002", Quantity = 3, UnitCost = 40.0 },
                new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 80.0 }
            ]
        };

        Assert.Equal(
            OdooJsonRpcService.ComputeCogsHash(request1),
            OdooJsonRpcService.ComputeCogsHash(request2));
    }

    [Fact]
    public void ComputeCogsHash_StockSumVsUnitCost_SameResult_ReturnsSameHash()
    {
        // StockSum=400 should equal UnitCost=80 * Quantity=5 = 400
        var request1 = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines = [new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, UnitCost = 80.0 }]
        };

        var request2 = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines = [new CogsJournalLineRequest { LineNum = 0, ItemCode = "ITEM001", Quantity = 5, StockSum = 400.0 }]
        };

        Assert.Equal(
            OdooJsonRpcService.ComputeCogsHash(request1),
            OdooJsonRpcService.ComputeCogsHash(request2));
    }

    [Fact]
    public void ComputeCogsHash_NoLineNum_SortsByItemCode()
    {
        // When LineNum is null, lines should still produce stable hash via ItemCode sort
        var request1 = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines =
            [
                new CogsJournalLineRequest { ItemCode = "ALPHA", Quantity = 1, UnitCost = 10.0 },
                new CogsJournalLineRequest { ItemCode = "BETA", Quantity = 2, UnitCost = 20.0 }
            ]
        };

        var request2 = new CogsJournalRequest
        {
            DocEntry = 700,
            Lines =
            [
                new CogsJournalLineRequest { ItemCode = "BETA", Quantity = 2, UnitCost = 20.0 },
                new CogsJournalLineRequest { ItemCode = "ALPHA", Quantity = 1, UnitCost = 10.0 }
            ]
        };

        Assert.Equal(
            OdooJsonRpcService.ComputeCogsHash(request1),
            OdooJsonRpcService.ComputeCogsHash(request2));
    }
}
