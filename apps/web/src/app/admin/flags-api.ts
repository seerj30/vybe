import { Injectable, inject } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { API } from "../api/api";

export type ReviewFlag = Schemas["ReviewFlag"];

/** Moderator read of the review flags (contract `adminListFlaggedRuns`; spec §17 decision 4). Bearer token only. */
@Injectable({ providedIn: "root" })
export class FlagsApi {
  private readonly api = inject(API);

  async list(flag: ReviewFlag | undefined, cursor?: string): Promise<Schemas["FlaggedRunPage"]> {
    return (await this.api.request<Schemas["FlaggedRunPage"]>("GET", "/admin/flagged-runs", { query: { flag, cursor } })).data;
  }
}
