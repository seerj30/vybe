import { HttpTransportType, HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { MessagePackHubProtocol } from "@microsoft/signalr-protocol-msgpack";
import type { ApiClient } from "../api/api-client";
import type { HubFactory, HubLike } from "./plaza-client";
import { HUB_URL } from "./protocol";

/** `POST /api/v1/spaces/tickets` (protocol §2): a one-time hub ticket through the typed client (cookie, CSRF). */
export async function createSpaceTicket(api: ApiClient): Promise<string> {
  const { data } = await api.request<{ ticket: string; expiresAt: string }>("POST", "/spaces/tickets");
  if (typeof data?.ticket !== "string" || !/^[A-Za-z0-9_-]{43}$/.test(data.ticket)) throw new Error("unexpected ticket");
  return data.ticket;
}

/**
 * The SignalR hub (protocol §2): WebSockets only, no negotiate request, MessagePack, a new ticket on every
 * connect. No automatic reconnect here: `PlazaClient` applies the §8 rules (kicks, backoff, the 5 minute limit).
 */
export function signalRFactory(ticket: () => Promise<string>): HubFactory {
  return (): HubLike => {
    const connection = new HubConnectionBuilder()
      .withUrl(HUB_URL, {
        transport: HttpTransportType.WebSockets,
        skipNegotiation: true,
        accessTokenFactory: ticket,
      })
      .withHubProtocol(new MessagePackHubProtocol())
      // Warnings and errors only: no URLs (the ticket is in the query string) and no message content.
      .configureLogging(LogLevel.Warning)
      .build();
    return {
      start: () => connection.start(),
      stop: () => connection.stop(),
      invoke: (method, ...args) => connection.invoke(method, ...args),
      send: (method, ...args) => connection.send(method, ...args),
      on: (name, handler) => connection.on(name, handler),
      onclose: (handler) => connection.onclose(handler),
    };
  };
}
