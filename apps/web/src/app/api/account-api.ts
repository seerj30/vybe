import { Injectable, inject } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { type RequestOptions, segment } from "./api-client";
import { API } from "./api";

export type AdminReportStatus = Schemas["ReportStatus"];

/**
 * Account, privacy and admin operations of the contract (spec §5 Identity, Safety, Quests and account,
 * Admin; ADR 0005). Admin operations accept only the bearer token of a signed-in staff account.
 */
@Injectable({ providedIn: "root" })
export class AccountApi {
  private readonly api = inject(API);

  /** Guest cookie only: the guest player of this browser, also while a Supabase session exists. */
  getGuest(): Promise<Schemas["Me"]> {
    return this.data("GET", "/me", { guestOnly: true });
  }

  /** J3: binds the guest of the cookie to the Supabase identity of `linkProof`. */
  linkAccount(linkProof: string): Promise<Schemas["AccountLinked"]> {
    return this.data("POST", "/accounts/link", { body: { linkProof }, guestOnly: true });
  }

  listBlocks(cursor?: string): Promise<Schemas["BlockedPlayerPage"]> {
    return this.data("GET", "/me/blocks", { query: { cursor } });
  }

  unblock(handle: string): Promise<void> {
    return this.data("DELETE", `/me/blocks/${segment(handle)}`);
  }

  requestDataExport(): Promise<Schemas["ExportRequested"]> {
    return this.data("POST", "/me/data-export");
  }

  /** Each call returns a new short-lived `downloadUrl` for a ready export. */
  getDataExport(exportId: string): Promise<Schemas["DataExport"]> {
    return this.data("GET", `/me/data-export/${segment(exportId)}`);
  }

  /** The key becomes the `requestId`; reuse it for every retry of one deletion. */
  requestDeletion(idempotencyKey: string): Promise<Schemas["DeletionRequested"]> {
    return this.data("POST", "/me/deletion-requests", { idempotencyKey });
  }

  /** No authentication: the request id is the capability. */
  getDeletionRequest(requestId: string): Promise<Schemas["DeletionRequest"]> {
    return this.data("GET", `/deletion-requests/${segment(requestId)}`, { guestOnly: true });
  }

  /** Active catalog items with ids, for the admin catalog form; staff accounts need no player. */
  adminCatalogItems(cursor?: string): Promise<Schemas["CatalogPage"]> {
    return this.data("GET", "/catalog/items", { query: { cursor, limit: 100 }, guestOnly: true });
  }

  adminListReports(status: AdminReportStatus | undefined, cursor?: string): Promise<Schemas["AdminReportPage"]> {
    return this.data("GET", "/admin/reports", { query: { status, cursor } });
  }

  adminResolveReport(reportId: string, body: Schemas["ReportResolveRequest"]): Promise<Schemas["AdminReport"]> {
    return this.data("POST", `/admin/reports/${segment(reportId)}/resolve`, { body });
  }

  adminSuspend(playerId: string, body: Schemas["SuspensionRequest"]): Promise<Schemas["Suspension"]> {
    return this.data("POST", `/admin/players/${segment(playerId)}/suspension`, { body });
  }

  adminLiftSuspension(playerId: string, body: Schemas["AdminReasonRequest"]): Promise<Schemas["Suspension"]> {
    return this.data("POST", `/admin/players/${segment(playerId)}/suspension/lift`, { body });
  }

  adminUpdateCatalogItem(itemId: string, body: Schemas["CatalogItemUpdateRequest"]): Promise<Schemas["AdminCatalogItem"]> {
    return this.data("PATCH", `/admin/catalog/items/${segment(itemId)}`, { body });
  }

  adminAdjustWallet(body: Schemas["WalletAdjustmentRequest"], idempotencyKey: string): Promise<Schemas["WalletAdjustment"]> {
    return this.data("POST", "/admin/wallet-adjustments", { body, idempotencyKey });
  }

  adminListAuditLogs(filter: { entityType?: string; entityId?: string; actor?: string }, cursor?: string): Promise<Schemas["AuditLogPage"]> {
    return this.data("GET", "/admin/audit-logs", { query: { ...filter, cursor } });
  }

  private async data<T>(method: string, path: string, options?: RequestOptions): Promise<T> {
    return (await this.api.request<T>(method, path, options)).data;
  }
}
