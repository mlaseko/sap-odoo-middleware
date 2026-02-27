using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Odoo;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Validates that the classic (non-Bearer) JSON-RPC path sends execute_kw with
/// args = [[id], vals] — not the double-wrapped [[[id], vals]] introduced by PR #41.
/// </summary>
public class OdooJsonRpcServiceClassicWriteTests
{
    /// <summary>
    /// Fake HttpMessageHandler that sequences through a list of pre-configured responses
    /// and records every request body for later inspection.
    /// </summary>
    private sealed class SequencedFakeHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public List<string> RequestBodies { get; } = [];

        public SequencedFakeHandler(IEnumerable<string> responses)
        {
            _responses = new Queue<string>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "";
            RequestBodies.Add(body);

            var responseJson = _responses.TryDequeue(out var next) ? next : """{"result":true}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }
    }

    private static OdooJsonRpcService BuildClassicService(SequencedFakeHandler handler)
    {
        var settings = Options.Create(new OdooSettings
        {
            BaseUrl = "http://odoo.test",
            Database = "testdb",
            UserName = "admin@test.com",
            Password = "secret",
            // No ApiKey → classic session auth, UseBearerAuth = false
        });

        var httpClient = new HttpClient(handler);
        return new OdooJsonRpcService(settings, httpClient, NullLogger<OdooJsonRpcService>.Instance);
    }

    [Fact]
    public async Task UpdateIncomingPaymentAsync_ClassicAuth_ExecuteKwArgsAreIdsThenVals()
    {
        // Arrange — two responses:
        //   1. /web/session/authenticate → uid = 2
        //   2. /jsonrpc execute_kw write → true
        var handler = new SequencedFakeHandler([
            """{"result":{"uid":2}}""",
            """{"result":true}"""
        ]);

        var service = BuildClassicService(handler);

        var request = new IncomingPaymentWriteBackRequest
        {
            OdooPaymentId = 42,
            SapDocEntry = 1001,
            SapDocNum = 2001
        };

        // Act
        await service.UpdateIncomingPaymentAsync(request);

        // Assert — we expect exactly 2 HTTP requests (auth + write)
        Assert.Equal(2, handler.RequestBodies.Count);

        // The second request is the execute_kw write call.
        var writeBody = JsonNode.Parse(handler.RequestBodies[1]);
        Assert.NotNull(writeBody);

        // Verify: params.method == "execute_kw"
        var paramsNode = writeBody!["params"];
        Assert.Equal("execute_kw", paramsNode?["method"]?.GetValue<string>());

        // params.args structure:
        //   [0] db, [1] uid, [2] password, [3] model, [4] method, [5] [id], [6] {vals}
        var args = paramsNode?["args"]?.AsArray();
        Assert.NotNull(args);

        // args[5] must be an array containing the single record id (not [[id], vals])
        var idsArg = args![5]?.AsArray();
        Assert.NotNull(idsArg);
        Assert.Equal(42, idsArg![0]!.GetValue<int>());

        // args[6] must be the vals dict (a JsonObject), not another nested array
        var valsArg = args[6]?.AsObject();
        Assert.NotNull(valsArg);
        Assert.Equal(1001, valsArg!["x_sap_inpay_docentry"]!.GetValue<int>());
        Assert.Equal(2001, valsArg["x_sap_inpay_docnum"]!.GetValue<int>());

        // Sanity-check: args must have exactly 7 elements (db/uid/pw/model/method/ids/vals)
        Assert.Equal(7, args.Count);
    }
}
