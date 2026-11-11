import { InjectionToken, inject } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { type ApiClient, segment } from "./api-client";
import { API } from "./api";

/**
 * Challenges, quests, profiles, safety and saved outfits (contract: packages/contracts). Uses the
 * shared client, so the cookie, CSRF header, timeouts and problem handling are the same.
 */
export class SocialApi {
  constructor(private readonly api: ApiClient) {}

  createChallenge(body: Schemas["ChallengeCreateRequest"], idempotencyKey: string): Promise<Schemas["ChallengeCreated"]> {
    return this.data("POST", "/challenges", { body, idempotencyKey });
  }

  createRematch(challengeId: string, body: Schemas["RematchRequest"], idempotencyKey: string): Promise<Schemas["ChallengeCreated"]> {
    return this.data("POST", `/challenges/${segment(challengeId)}/rematch`, { body, idempotencyKey });
  }

  getInvite(token: string): Promise<Schemas["ChallengePreview"]> {
    return this.data("GET", `/invites/${segment(token)}`);
  }

  acceptInvite(token: string, idempotencyKey: string): Promise<Schemas["ChallengeAccepted"]> {
    return this.data("POST", `/invites/${segment(token)}/accept`, { idempotencyKey });
  }

  getChallenge(challengeId: string): Promise<Schemas["Challenge"]> {
    return this.data("GET", `/challenges/${segment(challengeId)}`);
  }

  listMyChallenges(query: { cursor?: string; limit?: number } = {}): Promise<Schemas["ChallengePage"]> {
    return this.data("GET", "/me/challenges", { query });
  }

  listQuests(): Promise<Schemas["QuestList"]> {
    return this.data("GET", "/me/quests");
  }

  claimQuest(playerQuestId: string, idempotencyKey: string): Promise<Schemas["QuestClaimed"]> {
    return this.data("POST", `/me/quests/${segment(playerQuestId)}/claim`, { idempotencyKey });
  }

  /** P1 achievements (docs/design/p1-achievements.md): every achievement with the caller's server-computed progress. */
  listAchievements(): Promise<Schemas["AchievementList"]> {
    return this.data("GET", "/me/achievements");
  }

  getProfile(handle: string): Promise<Schemas["PublicProfile"]> {
    return this.data("GET", `/profiles/${segment(handle)}`);
  }

  listBlocks(query: { cursor?: string; limit?: number } = {}): Promise<Schemas["BlockedPlayerPage"]> {
    return this.data("GET", "/me/blocks", { query });
  }

  block(handle: string): Promise<void> {
    return this.data("POST", "/me/blocks", { body: { handle } });
  }

  unblock(handle: string): Promise<void> {
    return this.data("DELETE", `/me/blocks/${segment(handle)}`);
  }

  report(body: Schemas["ReportRequest"]): Promise<Schemas["ReportAccepted"]> {
    return this.data("POST", "/reports", { body });
  }

  listOutfits(): Promise<Schemas["OutfitList"]> {
    return this.data("GET", "/me/outfits");
  }

  saveOutfit(name: string): Promise<Schemas["OutfitCreated"]> {
    return this.data("POST", "/me/outfits", { body: { name } });
  }

  deleteOutfit(outfitId: string): Promise<void> {
    return this.data("DELETE", `/me/outfits/${segment(outfitId)}`);
  }

  applyOutfit(outfitId: string, expectedAvatarRevision: number): Promise<Schemas["EquipmentUpdated"]> {
    return this.data("POST", `/me/outfits/${segment(outfitId)}/apply`, { body: { expectedAvatarRevision } });
  }

  private async data<T>(method: string, path: string, options?: Parameters<ApiClient["request"]>[2]): Promise<T> {
    return (await this.api.request<T>(method, path, options)).data;
  }
}

export const SOCIAL_API = new InjectionToken<SocialApi>("SocialApi", { providedIn: "root", factory: () => new SocialApi(inject(API)) });
