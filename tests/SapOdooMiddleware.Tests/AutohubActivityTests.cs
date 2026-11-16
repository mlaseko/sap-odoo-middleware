using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services.Autohub;

namespace SapOdooMiddleware.Tests;

/// <summary>Phase 4: the invoice history is written after successful changes, in plain words.</summary>
public class AutohubActivityTests
{
    private static readonly Guid DocId = Guid.NewGuid();
    private static readonly Guid LineId = Guid.NewGuid();

    private sealed record Call(Guid Doc, string Action, string Detail, string Actor, IReadOnlyList<ActivityLine>? Lines);

    private static (AutohubActivityFilter Filter, List<Call> Calls) Build()
    {
        var calls = new List<Call>();
        var log = new Mock<IInvoiceActivityLog>();
        log.Setup(l => l.RecordAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((Guid d, string a, string t, string who, CancellationToken _) => calls.Add(new(d, a, t, who, null)))
            .Returns(Task.CompletedTask);
        log.Setup(l => l.RecordLinesAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<ActivityLine>>(), It.IsAny<CancellationToken>()))
            .Callback((Guid d, string a, string t, string who, IReadOnlyList<ActivityLine> ls, CancellationToken _) => calls.Add(new(d, a, t, who, ls)))
            .Returns(Task.CompletedTask);
        return (new AutohubActivityFilter(log.Object), calls);
    }

    private static async Task Run(AutohubActivityFilter filter, string action, string method,
        Dictionary<string, object?> args, IActionResult result)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        var descriptor = new ControllerActionDescriptor { RouteValues = new Dictionary<string, string?> { ["action"] = action } };
        var actionContext = new ActionContext(http, new RouteData(), descriptor);
        var executing = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), args, controller: new object());
        await filter.OnActionExecutionAsync(executing,
            () => Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()) { Result = result }));
    }

    [Fact]
    public async Task A_successful_line_change_is_recorded_in_plain_words()
    {
        var (filter, calls) = Build();
        await Run(filter, "MatchLine", "POST",
            new() { ["documentId"] = DocId, ["lineId"] = LineId, ["body"] = new PartsMatchRequest(" VK100 ") }, new OkObjectResult(null));

        var c = Assert.Single(calls);
        Assert.Equal(DocId, c.Doc);
        Assert.Equal("matched", c.Action);
        Assert.Equal("Matched to SAP item VK100.", c.Detail);
        Assert.Equal(LineId, Assert.Single(c.Lines!).LineId);
        Assert.Equal("operator", c.Actor);
    }

    [Fact]
    public async Task Failed_requests_and_reads_are_not_recorded()
    {
        var (filter, calls) = Build();
        await Run(filter, "MatchLine", "POST", new() { ["documentId"] = DocId, ["lineId"] = LineId,
            ["body"] = new PartsMatchRequest("X") }, new BadRequestObjectResult(new { error = "no" }));
        await Run(filter, "Get", "GET", new() { ["documentId"] = DocId }, new OkObjectResult(null));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Invoice_level_actions_use_the_result_counts()
    {
        var (filter, calls) = Build();
        await Run(filter, "BulkSkipPending", "POST", new() { ["documentId"] = DocId }, new OkObjectResult(new { skipped = 12 }));
        await Run(filter, "CompleteReview", "POST", new() { ["documentId"] = DocId }, new OkObjectResult(null));

        Assert.Equal(new[] { "Skipped 12 undecided line(s).", "Completed the review." }, calls.Select(c => c.Detail));
        Assert.All(calls, c => Assert.Null(c.Lines));
    }

    [Fact]
    public async Task Price_decisions_are_recorded_per_line_under_the_reviewer()
    {
        var (filter, calls) = Build();
        var other = Guid.NewGuid();
        var body = new AutohubPriceReviewController.DecisionsRequest
        {
            ReviewedBy = "Mohamed",
            Lines = new()
            {
                new() { LineId = LineId, Status = "approved", Pl03 = 150000m, Reason = "market" },
                new() { LineId = other, Status = "held" },
            },
        };
        var result = new PriceReviewDecideResult(2, Array.Empty<PriceReviewDecisionError>(), null);
        await Run(filter, "Decide", "POST", new() { ["documentId"] = DocId, ["request"] = body }, new OkObjectResult(result));

        var c = Assert.Single(calls);
        Assert.Equal("Mohamed", c.Actor);
        Assert.Equal("Price approved at PL03 150,000 (reason: market).", c.Lines!.Single(l => l.LineId == LineId).Detail);
        Assert.Equal("Price held.", c.Lines!.Single(l => l.LineId == other).Detail);
    }

    [Fact]
    public async Task Issues_bulk_records_only_the_lines_that_succeeded()
    {
        var (filter, calls) = Build();
        var ok = Guid.NewGuid();
        var result = new IssueBulkResult("assign_marque", 1, 1,
            new[] { new IssueBulkLineResult(ok, true, null, "create_new"), new IssueBulkLineResult(LineId, false, "no", "pending") }, null, null);
        var body = new AutohubIssuesController.BulkRequest { Action = "assign_marque", ManufacturerCode = "bm", ItemsGroupCode = 107 };
        await Run(filter, "Bulk", "POST", new() { ["documentId"] = DocId, ["body"] = body }, new OkObjectResult(result));

        var c = Assert.Single(calls);
        Assert.Equal(ok, Assert.Single(c.Lines!).LineId);
        Assert.Equal("Marque set to BM, SAP group 107 (from Issues).", c.Detail);
    }

    [Fact]
    public async Task Bulk_Create_records_each_created_line_with_its_item_code()
    {
        var line = Guid.NewGuid();
        var prov = new Mock<IPartsItemProvisioningService>();
        prov.Setup(p => p.ProvisionAsync(It.IsAny<PartsProvisioningLine>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<PartsLineOverride?>()))
            .ReturnsAsync(new PartsProvisioningOutcome("created", "VK10234", null));
        var repo = new Mock<IPartsReviewRepository>();
        repo.Setup(r => r.ListCreateNewAsync(DocId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PartsProvisioningLine(line, "A", new List<string>(), "VIKA", "P", 10m, true, null, null, null) });
        var docs = new Mock<IStagingPartsDocumentRepository>();
        var review = new Mock<IPriceReviewService>();
        IReadOnlyList<ActivityLine>? recorded = null;
        var log = new Mock<IInvoiceActivityLog>();
        log.Setup(l => l.RecordLinesAsync(DocId, "created", It.IsAny<string>(), "bulk create", It.IsAny<IReadOnlyList<ActivityLine>>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, string _, string _, string _, IReadOnlyList<ActivityLine> ls, CancellationToken _) => recorded = ls)
            .Returns(Task.CompletedTask);

        var svc = new PartsItemCreationService(prov.Object, repo.Object, docs.Object, review.Object,
            Options.Create(new AutohubPricingSettings()), NullLogger<PartsItemCreationService>.Instance, log.Object);
        await svc.BulkCreateAsync(DocId, CancellationToken.None);

        Assert.Equal("Created in SAP as VK10234.", Assert.Single(recorded!).Detail);
    }

    [Fact]
    public async Task Invoice_list_progress_counts_done_ready_and_attention()
    {
        var a = Guid.NewGuid();
        PartsLineState L(string status, int n, string? sku = "MB") => new(Guid.NewGuid(), n, "A", "P", "VIKA", 1m, 10m, false, status, null,
            "rapidapi_tecdoc_live", true, false, false, false, null, null, null, null, null, null, sku, 106, null, null, null, null, null, null);
        var lines = new Mock<IPartsLineStateRepository>();
        lines.Setup(r => r.ListForDocumentsAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, List<PartsLineState>>
            {
                [a] = new() { L("created", 1), L("matched", 2), L("skip", 3), L("create_new", 4), L("pending", 5),
                              L("create_new", 6, sku: null), L("create_new", 7, sku: "GEN") },
            });
        var svc = new InvoiceIssuesService(Mock.Of<IStagingPartsDocumentRepository>(), lines.Object, Mock.Of<IPartsReviewRepository>(),
            Mock.Of<IPriceReviewService>(), Mock.Of<IMarqueAssignmentService>(), Mock.Of<SapOdooMiddleware.Services.IAutohubSapB1Service>(),
            new AutohubBulkCreateJobService(Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                Mock.Of<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(), NullLogger<AutohubBulkCreateJobService>.Instance),
            Options.Create(new AutohubPricingSettings()));

        var p = (await svc.GetProgressAsync(new[] { a }, CancellationToken.None))[a];

        Assert.Equal((7, 3, 1, 3), (p.Total, p.Done, p.Ready, p.Attention));
        Assert.Equal("assign_marque", p.TopIssue!.Code);
        Assert.Equal(2, p.TopIssue.Count);
    }
}
