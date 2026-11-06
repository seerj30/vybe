using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vybe.Data;

namespace Vybe.Api.Tests;

/// <summary>Spec §5 Admin, §15 Admin ("moderator cannot change wallet", every action logged), E08 for admin routes.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AdminTests(ApiFactory api)
{
    [Fact]
    public async Task E08_AdminRoutes_NeedABearerTokenWithAnAppMetadataRole()
    {
        var guest = await Player.CreateGuestAsync(api);
        await Contract.AssertProblemAsync(await guest.Http.GetAsync("/api/v1/admin/reports"), HttpStatusCode.Unauthorized, "unauthenticated");

        using var player = Bearer(ApiFactory.SupabaseToken(Guid.NewGuid().ToString()));
        await Contract.AssertProblemAsync(await player.GetAsync("/api/v1/admin/reports"), HttpStatusCode.Forbidden, "forbidden");

        // user_metadata is writable by the user, so a role there grants nothing.
        using var forged = Bearer(ApiFactory.SupabaseToken(Guid.NewGuid().ToString(), new Dictionary<string, object>
        {
            ["user_metadata"] = new Dictionary<string, object> { ["vybe_role"] = "administrator" },
        }));
        await Contract.AssertProblemAsync(await forged.GetAsync("/api/v1/admin/audit-logs"), HttpStatusCode.Forbidden, "forbidden");

        // A moderator reviews reports but cannot change wallets, the catalog or read the audit log.
        using var moderator = Admin("moderator");
        await Contract.AssertAsync(await moderator.GetAsync("/api/v1/admin/reports"), HttpStatusCode.OK, "AdminReportPage");
        await Contract.AssertProblemAsync(
            await AdjustAsync(moderator, guest.Id, 100, "goodwill grant"), HttpStatusCode.Forbidden, "forbidden");
        await Contract.AssertProblemAsync(
            await moderator.PatchAsJsonAsync($"/api/v1/admin/catalog/items/{Guid.NewGuid()}", new { isActive = false, reason = "broken asset" }),
            HttpStatusCode.Forbidden, "forbidden");
        await Contract.AssertProblemAsync(await moderator.GetAsync("/api/v1/admin/audit-logs"), HttpStatusCode.Forbidden, "forbidden");

        // An administrator may also do moderator work.
        using var administrator = Admin("administrator");
        await Contract.AssertAsync(await administrator.GetAsync("/api/v1/admin/reports"), HttpStatusCode.OK, "AdminReportPage");
    }

    [Fact]
    public async Task Reports_QueueAndResolve_WithAudit()
    {
        var reporter = await Player.CreateGuestAsync(api);
        var target = await Player.CreateGuestAsync(api);
        var reportId = (await Contract.AssertAsync(
                await reporter.Http.PostAsJsonAsync("/api/v1/reports", new { targetHandle = await HandleAsync(target), reason = "harassment", details = "spam" }),
                HttpStatusCode.Accepted, "ReportAccepted"))
            .GetProperty("reportId").GetGuid();
        var (subject, moderator) = AdminWithSubject("moderator");
        using var _ = moderator;

        var queued = await FindReportAsync(moderator, "?status=open", reportId);
        Assert.Equal(target.Id, queued.GetProperty("target").GetProperty("playerId").GetGuid());
        Assert.Equal("spam", queued.GetProperty("details").GetString());

        await Contract.AssertProblemAsync(await ResolveAsync(moderator, reportId, "dismissed", "no"), HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(await ResolveAsync(moderator, reportId, "open", "still open"), HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(await ResolveAsync(moderator, Guid.NewGuid(), "dismissed", "not abusive"), HttpStatusCode.NotFound, "not_found");
        await Contract.AssertProblemAsync(await moderator.GetAsync("/api/v1/admin/reports?status=closed"), HttpStatusCode.UnprocessableEntity, "validation_failed");

        var resolved = await Contract.AssertAsync(await ResolveAsync(moderator, reportId, "dismissed", "not abusive"), HttpStatusCode.OK, "AdminReport");
        Assert.Equal("dismissed", resolved.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, resolved.GetProperty("reviewedAt").ValueKind);
        await FindReportAsync(moderator, "?status=dismissed", reportId);

        await using var db = api.CreateDbContext();
        var audit = await db.AdminAuditLogs.SingleAsync(a => a.EntityType == "player_report" && a.EntityId == reportId.ToString());
        Assert.Equal((subject, AdminRole.Moderator, "report.resolve", "not abusive"), (audit.ActorSubject, audit.ActorRole, audit.Action, audit.Reason));
        Assert.Equal(subject, (await db.PlayerReports.SingleAsync(r => r.Id == reportId)).ReviewedBy);
    }

    [Fact]
    public async Task Suspension_BlocksEconomicActions_AndLiftRestores_WithAudit()
    {
        var player = await Player.CreateGuestAsync(api);
        using var moderator = Admin("moderator");
        var until = api.Clock.GetUtcNow().UtcDateTime.AddDays(3);

        await Contract.AssertProblemAsync(
            await moderator.PostAsJsonAsync($"/api/v1/admin/players/{player.Id}/suspension", new { reason = "cheating", until = api.Clock.GetUtcNow().AddDays(-1) }),
            HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(
            await moderator.PostAsJsonAsync($"/api/v1/admin/players/{Guid.NewGuid()}/suspension", new { reason = "cheating" }),
            HttpStatusCode.NotFound, "not_found");

        var suspended = await Contract.AssertAsync(
            await moderator.PostAsJsonAsync($"/api/v1/admin/players/{player.Id}/suspension", new { reason = "cheating", until }),
            HttpStatusCode.OK, "Suspension");
        Assert.True(suspended.GetProperty("suspended").GetBoolean());
        Assert.Equal("suspended", (await player.MeAsync()).GetProperty("status").GetString());
        await Contract.AssertProblemAsync(
            await player.PurchaseAsync(await player.CatalogItemIdAsync("acc_sweatband")), HttpStatusCode.Forbidden, "account_suspended");

        var lifted = await Contract.AssertAsync(
            await moderator.PostAsJsonAsync($"/api/v1/admin/players/{player.Id}/suspension/lift", new { reason = "appeal accepted" }),
            HttpStatusCode.OK, "Suspension");
        Assert.False(lifted.GetProperty("suspended").GetBoolean());
        Assert.Equal("active", (await player.MeAsync()).GetProperty("status").GetString());

        await using var db = api.CreateDbContext();
        var actions = await db.AdminAuditLogs.Where(a => a.EntityType == "player" && a.EntityId == player.Id.ToString())
            .Select(a => a.Action).ToListAsync();
        Assert.Equal(["player.lift_suspension", "player.suspend"], actions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task CatalogToggle_HidesTheItemFromTheShop_WithAudit()
    {
        var player = await Player.CreateGuestAsync(api);
        var itemId = await player.CatalogItemIdAsync("emote_swish");
        using var administrator = Admin("administrator");
        try
        {
            var item = await Contract.AssertAsync(
                await administrator.PatchAsJsonAsync($"/api/v1/admin/catalog/items/{itemId}", new { isActive = false, reason = "broken asset" }),
                HttpStatusCode.OK, "AdminCatalogItem");
            Assert.False(item.GetProperty("isActive").GetBoolean());
            var shop = await player.Http.GetFromJsonAsync<JsonElement>("/api/v1/catalog/items?slot=emote");
            Assert.DoesNotContain(shop.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == itemId);
            await Contract.AssertProblemAsync(
                await administrator.PatchAsJsonAsync($"/api/v1/admin/catalog/items/{Guid.NewGuid()}", new { isActive = true, reason = "restock" }),
                HttpStatusCode.NotFound, "not_found");
        }
        finally
        {
            Assert.Equal(HttpStatusCode.OK, (await administrator.PatchAsJsonAsync(
                $"/api/v1/admin/catalog/items/{itemId}", new { isActive = true, reason = "asset fixed" })).StatusCode);
        }

        await using var db = api.CreateDbContext();
        Assert.True(await db.AdminAuditLogs.CountAsync(a => a.EntityType == "catalog_item" && a.EntityId == itemId.ToString()) >= 2);
    }

    [Fact]
    public async Task WalletAdjustment_OncePerKey_NeverNegative_WithAuditReference()
    {
        var player = await Player.CreateGuestAsync(api);
        using var administrator = Admin("administrator");
        var key = Guid.NewGuid();

        var first = await Contract.AssertAsync(await AdjustAsync(administrator, player.Id, 250, "event prize", key), HttpStatusCode.OK, "WalletAdjustment");
        var replay = await Contract.AssertAsync(await AdjustAsync(administrator, player.Id, 250, "event prize", key), HttpStatusCode.OK, "WalletAdjustment");
        Assert.Equal(first.GetProperty("transactionId").GetGuid(), replay.GetProperty("transactionId").GetGuid());
        Assert.Equal(250, first.GetProperty("balanceAfter").GetInt64());
        await Contract.AssertProblemAsync(
            await AdjustAsync(administrator, player.Id, 300, "event prize", key), HttpStatusCode.UnprocessableEntity, "idempotency_key_mismatch");
        await Contract.AssertProblemAsync(
            await AdjustAsync(administrator, player.Id, -251, "chargeback"), HttpStatusCode.Conflict, "insufficient_funds");
        await Contract.AssertProblemAsync(await AdjustAsync(administrator, player.Id, 0, "nothing"), HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(await AdjustAsync(administrator, player.Id, 5, "x"), HttpStatusCode.UnprocessableEntity, "validation_failed");
        await Contract.AssertProblemAsync(await AdjustAsync(administrator, Guid.NewGuid(), 5, "unknown player"), HttpStatusCode.NotFound, "not_found");
        var debit = await Contract.AssertAsync(await AdjustAsync(administrator, player.Id, -50, "chargeback"), HttpStatusCode.OK, "WalletAdjustment");
        Assert.Equal(200, debit.GetProperty("balanceAfter").GetInt64());
        Assert.Equal(200, (await player.MeAsync()).GetProperty("walletBalance").GetInt64());

        await using var db = api.CreateDbContext();
        var ledger = await db.WalletTransactions.SingleAsync(t => t.PlayerId == player.Id && t.LedgerKey == $"admin:{key}");
        Assert.Equal(LedgerReason.AdminAdjustment, ledger.Reason);
        var audit = await db.AdminAuditLogs.SingleAsync(a => a.Id == first.GetProperty("auditLogId").GetGuid());
        Assert.Equal((ledger.ReferenceId, "wallet.adjust", "event prize"), ((Guid?)audit.Id, audit.Action, audit.Reason));
        Assert.Equal(2, await db.WalletTransactions.CountAsync(t => t.PlayerId == player.Id));
    }

    [Fact]
    public async Task AuditLogSearch_FiltersByActorAndEntity_NewestFirst()
    {
        var player = await Player.CreateGuestAsync(api);
        var (subject, administrator) = AdminWithSubject("administrator");
        using var _ = administrator;
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await AdjustAsync(administrator, player.Id, i, $"grant {i}")).StatusCode);
            api.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var reasons = new List<string>();
        string? cursor = null;
        do
        {
            var query = $"/api/v1/admin/audit-logs?actor={subject}&entityType=player&entityId={player.Id}&limit=2" + (cursor is null ? string.Empty : $"&cursor={cursor}");
            var page = await Contract.AssertAsync(await administrator.GetAsync(query), HttpStatusCode.OK, "AuditLogPage");
            foreach (var entry in page.GetProperty("items").EnumerateArray())
            {
                reasons.Add(entry.GetProperty("reason").GetString()!);
                Assert.Equal(JsonValueKind.Object, entry.GetProperty("after").ValueKind);
            }

            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(["grant 3", "grant 2", "grant 1"], reasons);
        var none = await Contract.AssertAsync(await administrator.GetAsync("/api/v1/admin/audit-logs?actor=nobody"), HttpStatusCode.OK, "AuditLogPage");
        Assert.Empty(none.GetProperty("items").EnumerateArray());
    }

    private static async Task<string> HandleAsync(Player player) => (await player.MeAsync()).GetProperty("handle").GetString()!;

    private static async Task<JsonElement> FindReportAsync(HttpClient moderator, string query, Guid reportId)
    {
        string? cursor = null;
        do
        {
            var page = await Contract.AssertAsync(
                await moderator.GetAsync($"/api/v1/admin/reports{query}&limit=100" + (cursor is null ? string.Empty : $"&cursor={cursor}")),
                HttpStatusCode.OK, "AdminReportPage");
            foreach (var report in page.GetProperty("items").EnumerateArray())
            {
                if (report.GetProperty("reportId").GetGuid() == reportId)
                {
                    return report;
                }
            }

            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Fail($"report {reportId} not listed for {query}");
        return default;
    }

    private static async Task<HttpResponseMessage> ResolveAsync(HttpClient moderator, Guid reportId, string status, string reason) =>
        await moderator.PostAsJsonAsync($"/api/v1/admin/reports/{reportId}/resolve", new { status, reason });

    private static async Task<HttpResponseMessage> AdjustAsync(HttpClient admin, Guid playerId, long delta, string reason, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/wallet-adjustments")
        {
            Content = JsonContent.Create(new { playerId, delta, reason }),
        };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return await admin.SendAsync(request);
    }

    private HttpClient Admin(string role) => AdminWithSubject(role).Client;

    private (string Subject, HttpClient Client) AdminWithSubject(string role)
    {
        var subject = Guid.NewGuid().ToString();
        return (subject, Bearer(ApiFactory.SupabaseToken(subject, new Dictionary<string, object>
        {
            ["app_metadata"] = new Dictionary<string, object> { ["vybe_role"] = role },
        })));
    }

    private HttpClient Bearer(string token)
    {
        var client = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
