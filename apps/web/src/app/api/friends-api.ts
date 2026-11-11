import { InjectionToken, inject } from "@angular/core";
import type { Schemas } from "@vybe/contracts";
import { type ApiClient, segment } from "./api-client";
import { API } from "./api";

export type LeaderboardScope = Schemas["LeaderboardScope"];

/**
 * P1 friends and weekly leaderboard (contract: packages/contracts; design: docs/design/p1-friends-leaderboard.md).
 * Uses the shared client, so the cookie, CSRF header, timeouts and problem handling are the same.
 */
export class FriendsApi {
  constructor(private readonly api: ApiClient) {}

  listFriends(): Promise<Schemas["FriendList"]> {
    return this.data("GET", "/me/friends");
  }

  listRequests(): Promise<Schemas["FriendRequestLists"]> {
    return this.data("GET", "/me/friend-requests");
  }

  relation(handle: string): Promise<Schemas["FriendshipState"]> {
    return this.data("GET", `/me/friendships/${segment(handle)}`);
  }

  sendRequest(handle: string): Promise<Schemas["FriendRequestResult"]> {
    return this.data("POST", "/me/friend-requests", { body: { handle } });
  }

  accept(handle: string): Promise<Schemas["FriendRequestResult"]> {
    return this.data("POST", `/me/friend-requests/${segment(handle)}/accept`);
  }

  decline(handle: string): Promise<void> {
    return this.data("POST", `/me/friend-requests/${segment(handle)}/decline`);
  }

  cancel(handle: string): Promise<void> {
    return this.data("DELETE", `/me/friend-requests/${segment(handle)}`);
  }

  remove(handle: string): Promise<void> {
    return this.data("DELETE", `/me/friends/${segment(handle)}`);
  }

  weeklyLeaderboard(gameId: string, query: { scope?: LeaderboardScope; week?: string } = {}): Promise<Schemas["WeeklyLeaderboard"]> {
    return this.data("GET", `/leaderboards/${segment(gameId)}/weekly`, { query });
  }

  private async data<T>(method: string, path: string, options?: Parameters<ApiClient["request"]>[2]): Promise<T> {
    return (await this.api.request<T>(method, path, options)).data;
  }
}

export const FRIENDS_API = new InjectionToken<FriendsApi>("FriendsApi", { providedIn: "root", factory: () => new FriendsApi(inject(API)) });
